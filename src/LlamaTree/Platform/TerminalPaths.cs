namespace LlamaTree.Platform;

/// <summary>
/// File and folder paths written in a terminal, which the page turns into links: what one names, if it's there.
/// Terminal output is anyone's (a file being shown, a program's output), so only local paths are looked at: a network
/// path is never touched, not even to see if it exists, as on Windows that alone signs in to the server it names.
/// </summary>
internal static class TerminalPaths
{
    internal sealed record Found(string Path, bool IsFolder);

    /// <summary>The file or folder <paramref name="text"/> names, relative to <paramref name="baseDir"/> unless it's
    /// absolute (or starts with ~), or null when there's nothing there or it isn't a local path.</summary>
    public static Found? Resolve(string text, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024 || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        if (IsNetwork(text)) return null;
        var windows = OperatingSystem.IsWindows();
        // C:folder is relative to that drive's own current folder, which means nothing here.
        if (windows && text.Length >= 2 && text[1] == ':' && (text.Length == 2 || text[2] is not ('\\' or '/'))) return null;

        string full;
        try
        {
            var t = text;
            if (t == "~") t = Home;
            else if (t.StartsWith("~/", StringComparison.Ordinal) || (windows && t.StartsWith("~\\", StringComparison.Ordinal)))
                t = Path.Combine(Home, t[2..]);
            full = Path.GetFullPath(Path.IsPathFullyQualified(t) ? t : Path.Combine(baseDir, t));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        // Past the drive, a colon on Windows names an alternate data stream (file.txt:hidden): not a file to open.
        if (IsNetwork(full) || (windows && full.IndexOf(':', 2) >= 0)) return null;

        try
        {
            // A link to a network path counts as one (checking where it leads would reach the server).
            if (new FileInfo(full).LinkTarget is { } target && IsNetwork(target)) return null;
            if (File.Exists(full)) return new Found(full, false);
            if (Directory.Exists(full)) return new Found(PathText.TrimEnd(full), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>\\server\share, //server/share and \\?\ or \\.\ device paths (Windows; on macOS and Linux // is just /).</summary>
    private static bool IsNetwork(string path) =>
        OperatingSystem.IsWindows() && path.Length >= 2 && path[0] is ('\\' or '/') && path[1] is ('\\' or '/');
}
