using System.Runtime.InteropServices;
using LlamaTree.Terminal;

namespace LlamaTree.Platform;

/// <summary>Path details that differ between Windows (C:\work\repo) and macOS or Linux (/Users/me/repo).</summary>
internal static class PathText
{
    /// <summary>The path without a trailing separator; "/" stays "/".</summary>
    public static string TrimEnd(string path)
    {
        if (OperatingSystem.IsWindows()) return path.TrimEnd('\\');
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 && path.Length > 0 ? "/" : trimmed;
    }

    /// <summary>The folder's path ending in one separator, for testing whether another path is inside it.</summary>
    public static string WithSeparator(string folder)
    {
        var trimmed = TrimEnd(folder);
        return trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// The path the system actually opens for an existing file or folder, with links (and on Windows junctions)
    /// resolved, or null if there's nothing there.
    /// </summary>
    /// <param name="followFinalLink">
    /// False to identify the directory entry itself when the last component is a link (folders above it are still
    /// resolved): two different links to one file are then two different entries.
    /// </param>
    public static string? FinalPath(string path, bool followFinalLink = true)
    {
        if (OperatingSystem.IsWindows()) return Native.FinalPath(path, followFinalLink);
        if (!followFinalLink)
        {
            var parent = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (parent == null || name.Length == 0) return RealPath(path);
            var info = new FileInfo(path);
            if (!info.Exists && !Directory.Exists(path) && info.LinkTarget == null) return null;
            return RealPath(parent) is { } realParent ? Path.Combine(realParent, name) : null;
        }
        return RealPath(path);
    }

    private static string? RealPath(string path) => Libc.Instance.RealPath(path);

    /// <summary>
    /// <paramref name="path"/> with every link in it resolved, if it really is <paramref name="root"/> or inside it (also
    /// resolved); null if it isn't, or either isn't there. A link inside a folder can lead anywhere, so a path given as
    /// inside one is checked this way when it's given; the process is then started in the folder as it is at that moment
    /// (see Terminal.ConfinedFolder), against the root as resolved here (<see cref="ResolvedPath"/>).
    /// </summary>
    public static string? ResolvedInside(string path, string root) => Confine(path, root)?.Folder;

    /// <summary>
    /// The folder and the root it must be inside, both with every link resolved once, here and now, and the folder checked
    /// against that very root: null if either isn't there or the folder isn't inside. What's kept for the session (and
    /// checked again as its process starts) is this root, never one resolved again later, which could have gone or moved.
    /// </summary>
    public static (string Folder, string Root)? Confine(string path, string root) =>
        ResolvedPath(root) is { } realRoot && ResolvedPath(path) is { } real && IsInside(real, realRoot) ? (real, realRoot) : null;

    /// <summary>The path with every link in it resolved, as it's normally written; null if it isn't there.</summary>
    public static string? ResolvedPath(string path) => FinalPath(path) is { } real ? Plain(TrimEnd(real)) : null;

    /// <summary>
    /// Whether <paramref name="real"/> is <paramref name="realRoot"/> or inside it, both with their links already
    /// resolved. Compared exactly, letter case included, on every system: a volume (or, on Windows, a folder) can be case
    /// sensitive, where /work/Project and /work/project are two folders. Both are as the system resolved them, so the
    /// same folder is spelled the same; at worst a path is refused, never let out.
    /// </summary>
    public static bool IsInside(string real, string realRoot)
    {
        (real, realRoot) = (TrimEnd(real), TrimEnd(realRoot));
        var prefix = realRoot.EndsWith(Path.DirectorySeparatorChar) ? realRoot : realRoot + Path.DirectorySeparatorChar;   // "/" is its own
        return string.Equals(real, realRoot, StringComparison.Ordinal) || real.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>A path as Windows resolves it, \\?\C:\... (or \\?\UNC\server\share\...), as it's normally written.</summary>
    public static string Plain(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    /// <summary>
    /// Whether two full paths name the same place, also when one reaches it through a link: git reports real paths,
    /// and on a Mac the temporary folder /var/... is really /private/var/.... A path that's gone is compared through
    /// the nearest folder above it that's still there.
    /// </summary>
    public static bool SamePlace(string a, string b)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var (fullA, fullB) = (TrimEnd(Path.GetFullPath(a)), TrimEnd(Path.GetFullPath(b)));
        return string.Equals(fullA, fullB, comparison) || string.Equals(Resolved(fullA), Resolved(fullB), comparison);
    }

    /// <summary>The path with the links in the part of it that exists resolved; the rest as it was.</summary>
    private static string Resolved(string full)
    {
        var rest = new Stack<string>();
        for (var at = full; ; )
        {
            if (FinalPath(at) is { } real) return TrimEnd(rest.Aggregate(real, Path.Combine));
            var parent = Path.GetDirectoryName(at);
            if (parent == null) return full;
            rest.Push(Path.GetFileName(at));
            at = parent;
        }
    }

    // ---------------------------------------------------------------- the bin

    /// <summary>
    /// Moves a file to the Recycle Bin (Windows), the Trash (macOS) or the desktop's trash (Linux: gio, else the
    /// freedesktop.org trash folder), so an undo can be undone.
    /// </summary>
    public static void MoveToTrash(string full)
    {
        if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            // Through the Finder's own API: ~/.Trash itself is off limits to apps without Full Disk Access, and a file on
            // another volume goes to that volume's trash. Put Back works afterwards too.
            if (Desktop.Capture("/usr/bin/osascript", ["-l", "JavaScript", "-e", MacTrashScript, full])?.Trim() != "ok")
                throw new IOException($"Couldn't move {full} to the Trash.");
            return;
        }
        if (Desktop.Capture("gio", ["trash", "--", full]) != null && !File.Exists(full)) return;
        // https://specifications.freedesktop.org/trash-spec/: the file goes in files/, with a note of where it was in info/.
        var trash = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(home, ".local", "share"), "Trash");
        Directory.CreateDirectory(Path.Combine(trash, "info"));
        var moved = MoveUnique(full, Path.Combine(trash, "files"));
        File.WriteAllText(Path.Combine(trash, "info", Path.GetFileName(moved) + ".trashinfo"),
            $"[Trash Info]\nPath={Uri.EscapeDataString(full).Replace("%2F", "/")}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
    }

    private const string MacTrashScript = """
        ObjC.import('Foundation');
        function run(argv) {
          const url = $.NSURL.fileURLWithPath(argv[0]);
          return $.NSFileManager.defaultManager.trashItemAtURLResultingItemURLError(url, null, null) ? 'ok' : 'failed';
        }
        """;

    /// <summary>Moves the file into <paramref name="folder"/>, under a new name if one of that name is already there. Returns where it went.</summary>
    private static string MoveUnique(string file, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        var target = Path.Combine(folder, name + ext);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++) target = Path.Combine(folder, $"{name} {n}{ext}");
        File.Move(file, target);
        return target;
    }
}
