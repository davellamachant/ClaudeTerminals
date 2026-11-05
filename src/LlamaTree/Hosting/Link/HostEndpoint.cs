using System.IO.Pipes;

namespace LlamaTree.Hosting.Link;

/// <summary>
/// Where the window finds the session host: a named pipe on Windows, a Unix socket on macOS and Linux, one per profile.
/// Both are opened with <see cref="PipeOptions.CurrentUserOnly"/>: only a process of this user can connect, and the
/// window only talks to a host run by this user.
/// </summary>
internal static class HostEndpoint
{
    /// <summary>The pipe's name on Windows; on macOS and Linux, the socket's path (a rooted name is used as the path as it is).</summary>
    public static string Name { get; } = MakeName();

    private static string MakeName()
    {
        var name = "LlamaTree.Host" + AppProfile.InstanceSuffix;
        if (OperatingSystem.IsWindows()) return name;
        // A folder only this user can use: the login session's runtime folder on Linux, the per-user temp folder on
        // macOS. A socket's path has to stay short (104 bytes on macOS), so not the profile folder.
        var folder = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime)
            ? runtime : Path.GetTempPath();
        return Path.Combine(folder, name.ToLowerInvariant() + ".sock");
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

    /// <summary>A connection to this profile's session host, or null if none answers within <paramref name="timeout"/>.</summary>
    public static async Task<Stream?> ConnectAsync(TimeSpan timeout)
    {
        var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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
