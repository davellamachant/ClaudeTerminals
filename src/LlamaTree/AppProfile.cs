namespace LlamaTree;

/// <summary>
/// Where the app keeps its data. The app was called Claude Terminals until 1.2: its folders and environment
/// variables had that name, so the old ones are still read and the folders are moved to the new name once.
/// </summary>
internal static class AppProfile
{
    private const string Name = "LlamaTree";
    private const string OldName = "ClaudeTerminals";

    /// <summary>A separate profile's folder (LLAMATREE_DATA, or CLAUDE_TERMINALS_DATA from before the rename), or null.</summary>
    public static string? CustomFolder { get; } = Variable("LLAMATREE_DATA", "CLAUDE_TERMINALS_DATA");

    /// <summary>
    /// "" for the default profile; for a separate one, "." and a short hash of its folder, so its single-instance locks
    /// and the window's connection to its session host are its own.
    /// </summary>
    public static string InstanceSuffix { get; } = string.IsNullOrEmpty(CustomFolder) ? ""
        : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(CustomFolder.ToUpperInvariant())))[..12];

    /// <summary>An environment variable, or the one it was called before the rename; null when neither is set.</summary>
    public static string? Variable(string name, string oldName) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
        : Environment.GetEnvironmentVariable(oldName) is { Length: > 0 } old ? old
        : null;

    /// <summary>%APPDATA%\LlamaTree: sessions, projects, panels, plugins.</summary>
    public static string RoamingFolder => roaming.Value;

    /// <summary>%LOCALAPPDATA%\LlamaTree: the browser's data.</summary>
    public static string LocalFolder => local.Value;

    private static readonly Lazy<string> roaming = new(() => Resolve(Environment.SpecialFolder.ApplicationData));
    private static readonly Lazy<string> local = new(() => Resolve(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// Moves both folders from their old name the first time this version runs. Called once this copy holds the
    /// single-instance lock, so no other copy is using them.
    /// </summary>
    public static void MoveFromOldName()
    {
        if (CustomFolder != null) return;   // a separate profile is a folder of its own, whatever it's called
        _ = RoamingFolder;
        _ = LocalFolder;
    }

    /// <summary>The folder under <paramref name="root"/>, moved from its old name if only that exists. If it can't be moved, the old one is used as it is.</summary>
    internal static string Resolve(Environment.SpecialFolder root) => Resolve(Environment.GetFolderPath(root));

    internal static string Resolve(string root)
    {
        var folder = Path.Combine(root, Name);
        var old = Path.Combine(root, OldName);
        if (Directory.Exists(folder) || !Directory.Exists(old)) return folder;
        try
        {
            Directory.Move(old, folder);
            return folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return old;   // in use or locked: carry on there, and try again next time
        }
    }
}
