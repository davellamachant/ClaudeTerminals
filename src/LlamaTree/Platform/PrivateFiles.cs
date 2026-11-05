namespace LlamaTree.Platform;

/// <summary>
/// Folders and files only this user can read: on macOS and Linux a folder is 0700 and a file 0600, whatever the umask,
/// since a session's token (mcp-*.json) would otherwise be readable by another user of the computer, who could then
/// call the panel server as that session. On Windows the profile's folders are already this user's, so these are
/// the ordinary calls.
/// </summary>
internal static class PrivateFiles
{
    public const UnixFileMode FolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode DataFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Makes the folder (and any it's in that aren't there yet) 0700; one that's already there is made 0700 too.</summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        // .NET gives the mode only to the last folder (those it's in get the umask's), so each is made on its own.
        var missing = new Stack<string>();
        for (var d = Path.GetFullPath(path); d != null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
        foreach (var d in missing) Directory.CreateDirectory(d, FolderMode);
        Restrict(path, FolderMode);   // one made before this version (or under another umask) was 0755
    }

    /// <summary>Writes the file as 0600 from the moment it's made, so the text is never in a file others can read.</summary>
    public static void WriteAllText(string path, string text)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = DataFileMode;
            if (File.Exists(path)) Restrict(path, DataFileMode);   // the mode is only for a new file: an old one is narrowed before the text goes in
        }
        using var writer = new StreamWriter(path, options);
        writer.Write(text);
    }

    /// <summary>
    /// At start, makes the folder 0700 and the files in it whose names match <paramref name="pattern"/> 0600 (from before
    /// this version they were made with the umask's modes). Nothing on Windows, or if the folder isn't there.
    /// </summary>
    public static void Tighten(string folder, string? pattern = null)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(folder)) return;
        try
        {
            Restrict(folder, FolderMode);
            if (pattern == null) return;
            foreach (var file in Directory.EnumerateFiles(folder, pattern)) Restrict(file, DataFileMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Someone else's, or gone: left as it is.
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Restrict(string path, UnixFileMode mode)
    {
        if (File.GetUnixFileMode(path) != mode) File.SetUnixFileMode(path, mode);
    }
}
