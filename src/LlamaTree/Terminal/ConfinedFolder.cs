using Microsoft.Win32.SafeHandles;
using LlamaTree.Platform;

namespace LlamaTree.Terminal;

/// <summary>
/// The folder a session started by another session's agent runs in, held from the moment it's checked until its process
/// has started there (see SessionInfo.Within). Checking a path and then starting a process at it leaves a moment in which
/// a folder on the way could be renamed and a link put in its place; held, it can't be swapped:
/// <list type="bullet">
/// <item>macOS and Linux: the folder is opened, its path read from the open folder (where it really is), and the
/// process started in that open folder (posix_spawn's fchdir), not at a path.</item>
/// <item>Windows: every folder on its real path, from the drive down, is opened without letting it be renamed or
/// deleted, its path read from the open folder, and the process started at that path while they're held.</item>
/// </list>
/// Either way the folder must be inside <c>within</c>, the allowed folder as it resolved when it was allowed.
/// </summary>
internal sealed unsafe class ConfinedFolder : IDisposable
{
    private readonly List<SafeFileHandle> held = [];

    /// <summary>Where it really is, as it's normally written.</summary>
    public string Path { get; private set; } = "";

    /// <summary>macOS and Linux: the open folder, for the process to start in; -1 on Windows.</summary>
    public int Descriptor { get; private set; } = -1;

    private ConfinedFolder() { }

    /// <summary>Opens and checks <paramref name="directory"/>; throws <see cref="IOException"/> when it isn't inside <paramref name="within"/> (or isn't there).</summary>
    public static ConfinedFolder Open(string directory, string within)
    {
        var folder = new ConfinedFolder();
        try
        {
            if (OperatingSystem.IsWindows()) folder.HoldWindows(directory, within);
            else folder.OpenUnix(directory, within);
            return folder;
        }
        catch
        {
            folder.Dispose();
            throw;
        }
    }

    private static IOException Outside(string directory, string within) =>
        new($"{directory} isn't inside {within} any more (a folder on the way leads elsewhere), so the session wasn't started.");

    private void OpenUnix(string directory, string within)
    {
        var libc = Libc.Instance;
        if (libc.posix_spawn_file_actions_addfchdir_np == null)
            throw new IOException("This system can't start a process in an open folder, so a session confined to a folder can't be started safely.");
        var bytes = System.Text.Encoding.UTF8.GetBytes(directory + "\0");
        fixed (byte* p = bytes) Descriptor = libc.open(p, Libc.O_RDONLY | libc.O_DIRECTORY | libc.O_CLOEXEC);
        if (Descriptor < 0) throw new IOException($"{directory} isn't there.");
        Path = libc.PathOf(Descriptor) ?? throw new IOException($"Where {directory} is couldn't be told.");
        if (!PathText.IsInside(Path, within)) throw Outside(directory, within);
    }

    private void HoldWindows(string directory, string within)
    {
        var real = PathText.ResolvedPath(directory) ?? throw new IOException($"{directory} isn't there.");
        if (!PathText.IsInside(real, within)) throw Outside(directory, within);
        // Each folder from the drive down: none a link, and each where it was resolved, once all are held (one swapped
        // between resolving and holding shows here).
        var root = System.IO.Path.GetPathRoot(real) ?? throw Outside(directory, within);
        var path = root;
        foreach (var part in real[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Prepend(""))
        {
            path = part.Length == 0 ? path : System.IO.Path.Combine(path, part);
            var handle = Native.HoldFolder(path) ?? throw Outside(directory, within);
            held.Add(handle);
            if (part.Length > 0 && Native.IsLink(handle)) throw Outside(directory, within);
        }
        var final = Native.FinalPathOf(held[^1]) is { } f ? PathText.Plain(PathText.TrimEnd(f)) : null;
        if (final == null || !string.Equals(final, real, StringComparison.Ordinal) || !PathText.IsInside(final, within)) throw Outside(directory, within);
        Path = final;
    }

    public void Dispose()
    {
        foreach (var h in held) h.Dispose();
        held.Clear();
        if (Descriptor >= 0) Libc.Instance.close(Descriptor);
        Descriptor = -1;
    }
}
