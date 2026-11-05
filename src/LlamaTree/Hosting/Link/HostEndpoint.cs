using System.IO.Pipes;
using System.Text;
using LlamaTree.Terminal;

namespace LlamaTree.Hosting.Link;

/// <summary>
/// Where the window finds the session host: a named pipe on Windows, a Unix socket on macOS and Linux, one per profile.
/// Both are opened with <see cref="PipeOptions.CurrentUserOnly"/>: only a process of this user can connect, and the
/// window only talks to a host run by this user.
/// </summary>
internal static class HostEndpoint
{
    private static string? name;

    /// <summary>
    /// The pipe's name on Windows; on macOS and Linux, the socket's path (a rooted name is used as the path as it is).
    /// Throws <see cref="IOException"/> when there's no folder only this user can use for it (see <see cref="SocketPath"/>);
    /// asked again, it's looked for again.
    /// </summary>
    public static string Name => name ??= MakeName();

    /// <summary>Why the host's socket has no path, said so the person can put it right; null when it has one.</summary>
    public static string? Problem
    {
        get
        {
            try { _ = Name; return null; }
            catch (IOException ex) { return ex.Message; }
        }
    }

    private static string MakeName()
    {
        var name = "LlamaTree.Host" + AppProfile.InstanceSuffix;
        if (OperatingSystem.IsWindows()) return name;
        return SocketPath(name.ToLowerInvariant() + ".sock",
            [Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), Environment.GetEnvironmentVariable("TMPDIR")],
            () => Path.Combine(AppProfile.LocalFolder, "run"), "/tmp", Libc.Instance.EffectiveUserId(), OperatingSystem.IsMacOS() ? 103 : 107);
    }

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Others = (UnixFileMode)0x3F;   // group and other: read, write, search

    /// <summary>
    /// The socket's path on macOS and Linux: in a folder only user <paramref name="uid"/> can use, so another account
    /// can't take the path first (which would keep the host from listening) or reach the socket. Best first:
    /// <list type="number">
    /// <item>one of <paramref name="privateFolders"/> that's already private: the login session's runtime folder
    /// ($XDG_RUNTIME_DIR, Linux), the per-user temp folder ($TMPDIR, macOS). Each must be a real folder (not a link),
    /// owned by the user, with no access for anyone else. A shared $TMPDIR like /tmp is never used.</item>
    /// <item><paramref name="dataFolder"/> (the app's local data folder's "run"), made 0700.</item>
    /// <item>when that path would be too long for a socket (<paramref name="maxBytes"/>: 103 on macOS, 107 on Linux),
    /// <paramref name="sharedRoot"/>/llamatree-&lt;uid&gt; (/tmp), made 0700 and checked: a real folder, the user's, 0700.
    /// If another account has it, or it's a link, this throws <see cref="IOException"/> saying so.</item>
    /// </list>
    /// The session host (listening) and the window or a server's bridge (connecting) all come here, so they agree on it.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string SocketPath(string fileName, IEnumerable<string?> privateFolders, Func<string> dataFolder, string sharedRoot, uint uid, int maxBytes)
    {
        bool Fits(string folder) => Encoding.UTF8.GetByteCount(Path.Combine(folder, fileName)) <= maxBytes;

        foreach (var candidate in privateFolders)
        {
            if (candidate is not { Length: > 0 } || !Path.IsPathFullyQualified(candidate)) continue;
            var folder = Path.TrimEndingDirectorySeparator(candidate);
            if (Fits(folder) && Owned(folder, uid, out var mode) == null && (mode & Others) == 0) return Path.Combine(folder, fileName);
        }

        var data = Path.TrimEndingDirectorySeparator(dataFolder());
        if (Fits(data) && MakePrivate(data, uid) == null) return Path.Combine(data, fileName);

        var shared = Path.Combine(sharedRoot, "llamatree-" + uid);
        if (!Fits(shared)) throw new IOException($"LlamaTree's session host has no folder for its socket: {data} and {shared} are too long for one. Set XDG_RUNTIME_DIR to a short folder only you can use.");
        if (MakePrivate(shared, uid) is { } why)
            throw new IOException($"LlamaTree's session host can't use {shared} for its socket: it {why}. Remove it, or set XDG_RUNTIME_DIR to a folder only you can use.");
        return Path.Combine(shared, fileName);
    }

    /// <summary>
    /// Makes <paramref name="folder"/> if it's missing (0700) and closes it up to 0700. Null when it's then a real folder
    /// of <paramref name="uid"/>'s with no access for anyone else; else why not.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string? MakePrivate(string folder, uint uid)
    {
        try
        {
            var parent = Path.GetDirectoryName(folder);
            if (parent != null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
            // Made only where nothing is, not even a link: Directory.Exists looks through one, a lone link is told by its target.
            if (!Directory.Exists(folder) && new FileInfo(folder).LinkTarget == null) Directory.CreateDirectory(folder, OwnerOnly);
            if (Owned(folder, uid, out var mode) is { } why) return why;
            if (mode == OwnerOnly) return null;
            // The user's own folder, but others could get in. In /tmp (sticky) or the user's home no one else can swap it
            // for a link meanwhile, so it's closed up where it is.
            File.SetUnixFileMode(folder, OwnerOnly);
            return Owned(folder, uid, out mode) ?? (mode == OwnerOnly ? null : "couldn't be made private (mode 0700)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "couldn't be made or checked (" + ex.Message + ")";
        }
    }

    /// <summary>
    /// Null when <paramref name="folder"/> itself (a link not followed) is a real folder owned by <paramref name="uid"/>,
    /// with its permission bits in <paramref name="mode"/>; else why not.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string? Owned(string folder, uint uid, out UnixFileMode mode)
    {
        mode = 0;
        var libc = Libc.Instance;
        if (libc.CanLStat)
        {
            if (!libc.LStat(folder, out var owner, out var st)) return "isn't there";
            if ((st & 0xF000) == 0xA000) return "is a link";
            if ((st & 0xF000) != 0x4000) return "isn't a folder";
            if (owner != uid) return $"belongs to another account (uid {owner})";
            mode = (UnixFileMode)(st & 0x1FF);
            return null;
        }
        // Where struct stat isn't known here: only the owner (or root) may change a mode, so setting it to what it is
        // tells whether this process owns it.
        var info = new DirectoryInfo(folder);
        if (info.LinkTarget != null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))) return "is a link";
        if (!info.Exists) return "isn't there";
        mode = File.GetUnixFileMode(folder) & (UnixFileMode)0x1FF;
        try { File.SetUnixFileMode(folder, mode); }
        catch (UnauthorizedAccessException) { return "belongs to another account"; }
        return null;
    }

    /// <summary>Whether a window can still find the host: on macOS and Linux, whether the socket file is still there.</summary>
    public static bool IsListening => OperatingSystem.IsWindows() || File.Exists(Name);

    /// <summary>A server end, waiting for a window. More can be made, so a new window can connect while one is still attached.</summary>
    public static NamedPipeServerStream Listen()
    {
        if (!OperatingSystem.IsWindows() && File.Exists(Name))
        {
            // Left behind by a host that ended without tidying up; this host holds the host lock, so it's no one's.
            try { File.Delete(Name); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    /// <summary>
    /// A connection to this profile's session host, or null if none answers within <paramref name="timeout"/> (or there's
    /// no path for one: see <see cref="Problem"/>). On macOS and Linux, when nothing answers at <see cref="Name"/>, a host
    /// from before the socket left the shared temp folder is looked for where it listened (see <see cref="LegacyName"/>).
    /// </summary>
    public static async Task<Stream?> ConnectAsync(TimeSpan timeout)
    {
        string? name = null;
        try { name = Name; }
        catch (IOException) { }
        if (name != null && await ConnectAsync(name, timeout) is { } stream) return stream;
        if (OperatingSystem.IsWindows() || LegacySocket(LegacyName(), Libc.Instance.EffectiveUserId()) is not { } legacy || legacy == name) return null;
        // A live host answers at once: a socket file left behind isn't waited on long.
        return await ConnectAsync(legacy, TimeSpan.FromMilliseconds(Math.Min(timeout.TotalMilliseconds, 100)));
    }

    /// <summary>
    /// Where the host listened before its socket was kept out of the shared temp folder: the runtime folder, else the
    /// temp folder (/tmp on Linux). Only ever connected to, never listened on: a host still running from before an
    /// update that didn't end it (apt upgrade on a server, say) stays reachable until it ends, then everything is at
    /// <see cref="Name"/>.
    /// </summary>
    private static string LegacyName()
    {
        var folder = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime)
            ? runtime : Path.GetTempPath();
        return Path.Combine(folder, ("LlamaTree.Host" + AppProfile.InstanceSuffix).ToLowerInvariant() + ".sock");
    }

    /// <summary>
    /// <paramref name="path"/> if it is itself (not a link) a socket owned by <paramref name="uid"/>: in a shared folder
    /// anyone can make a file of that name, and the window mustn't talk to another account's. Null otherwise, or where
    /// that can't be told.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string? LegacySocket(string path, uint uid)
    {
        var libc = Libc.Instance;
        return libc.CanLStat && libc.LStat(path, out var owner, out var mode) && (mode & 0xF000) == 0xC000 && owner == uid ? path : null;
    }

    private static async Task<Stream?> ConnectAsync(string name, TimeSpan timeout)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync((int)timeout.TotalMilliseconds);
            return client;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            await client.DisposeAsync();
            return await ConnectAsUserAsync(timeout);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await client.DisposeAsync();
            return null;
        }
    }

    /// <summary>
    /// On Windows, CurrentUserOnly checks that the pipe's owner is this process's token's default owner, which in an
    /// administrator's elevated token (an SSH sign-in's, say) is the Administrators group rather than the user, while
    /// the host on the desktop made its pipe as the user. This checks the pipe's owner is this user instead: connected
    /// to without the option (only this user may: the host's pipe lets no one else in), then refused unless it is.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<Stream?> ConnectAsUserAsync(TimeSpan timeout)
    {
        var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync((int)timeout.TotalMilliseconds);
            var owner = client.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (owner != null && owner.Equals(me.User)) return client;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        await client.DisposeAsync();
        return null;
    }
}
