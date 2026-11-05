using System.Text.Json;
using LlamaTree.Platform;

namespace LlamaTree.Sessions;

internal sealed class SessionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Command { get; set; } = "claude";
    /// <summary>Id of the project this session belongs to, or null for none (shown whichever projects are selected).</summary>
    public string? Project { get; set; }
    /// <summary>
    /// The Claude conversation this session reopens (a session id), chosen by the app when the session is made and
    /// followed when Claude moves to another one (/clear, /resume). The app starts Claude with --resume for it, or with
    /// --session-id before anything has been saved. Null until known, e.g. for a session made with --continue.
    /// </summary>
    public string? Conversation { get; set; }
    /// <summary>
    /// The repository Git Changes shows, when the person picked one other than the folder's own (e.g. a session one
    /// folder above several repositories). Null for the default: the folder's repository, or the first one inside it.
    /// </summary>
    public string? GitRepo { get; set; }
    /// <summary>
    /// For a session that runs on a server: how it's reached (user@server, or a Host from the SSH config). Null for one
    /// on this computer. A server's session is run by that server's LlamaTree; this one keeps where it goes among the
    /// others, its project, and what it was last seen as (shown while the server can't be reached).
    /// </summary>
    public string? Server { get; set; }
    /// <summary>The session's id on <see cref="Server"/>.</summary>
    public string? RemoteId { get; set; }
    /// <summary>The session whose agent started this one (start_session), which may drive it; null for one you made.</summary>
    public string? StartedBy { get; set; }
    /// <summary>
    /// For a session another session's agent started in a folder it was allowed: that allowed folder, as it resolved then.
    /// Its process starts only if its folder is really inside it at that moment, and in that very folder (see
    /// Terminal.ConfinedFolder); null for any other session.
    /// </summary>
    public string? Within { get; set; }
}

internal sealed partial class ProjectInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>A chosen colour as #rrggbb, or null for the automatic one (derived from the id).</summary>
    public string? Color { get; set; }

    [System.Text.RegularExpressions.GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial System.Text.RegularExpressions.Regex HexColor();

    /// <summary>The colour as lowercase #rrggbb, or null if it isn't one (which means automatic).</summary>
    public static string? NormalizeColor(string? color) =>
        color != null && HexColor().IsMatch(color) ? color.ToLowerInvariant() : null;
}

/// <summary>Persists the project list (in display order) to projects.json, next to sessions.json.</summary>
internal static class ProjectStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(SessionStore.AppDataFolder, "projects.json");

    public static List<ProjectInfo> Load() => Load(FilePath);

    internal static List<ProjectInfo> Load(string path)
    {
        // A corrupt file is set aside and the copy before it used (see JsonFile); at worst it loses the grouping, never
        // the sessions themselves.
        var projects = (JsonFile.Read<List<ProjectInfo?>>(path) ?? [])
            .OfType<ProjectInfo>()
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
            .DistinctBy(p => p.Id)
            .ToList();
        // The page puts the colour straight into a style, so only a plain #rrggbb ever reaches it.
        foreach (var p in projects) p.Color = ProjectInfo.NormalizeColor(p.Color);
        return projects;
    }

    public static void Save(IEnumerable<ProjectInfo> projects) => Save(FilePath, projects);

    internal static void Save(string path, IEnumerable<ProjectInfo> projects)
    {
        Platform.PrivateFiles.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonFile.Write(path, JsonSerializer.Serialize(projects, Options));
    }
}

/// <summary>
/// Persists the configured sessions to %APPDATA%\LlamaTree\sessions.json
/// (or the folder named by LLAMATREE_DATA, for separate profiles/testing).
/// </summary>
internal static class SessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string AppDataFolder => AppProfile.CustomFolder ?? AppProfile.RoamingFolder;

    private static string FilePath => Path.Combine(AppDataFolder, "sessions.json");

    public static List<SessionInfo> Load() => Load(FilePath);

    /// <summary>
    /// The saved sessions. A corrupt file doesn't stop the app from starting, and isn't saved over: it's set aside and
    /// the copy before it (sessions.json.bak) used, if there is one (see JsonFile). Entries that are null or have no
    /// id, folder or command are dropped, as is a second one with the same id.
    /// </summary>
    internal static List<SessionInfo> Load(string path)
    {
        var sessions = (JsonFile.Read<List<SessionInfo?>>(path, Options) ?? [])
            .OfType<SessionInfo>()
            .Where(s => !string.IsNullOrWhiteSpace(s.Id) && s.Directory != null && s.Command != null)
            .DistinctBy(s => s.Id)
            .ToList();
        foreach (var s in sessions) s.Name ??= "";
        return sessions;
    }

    public static void Save(IEnumerable<SessionInfo> sessions)
    {
        Platform.PrivateFiles.CreateDirectory(AppDataFolder);
        Save(FilePath, sessions);
    }

    /// <summary>Writes the sessions in one step, keeping the file they replace as sessions.json.bak.</summary>
    internal static void Save(string path, IEnumerable<SessionInfo> sessions) =>
        JsonFile.Write(path, JsonSerializer.Serialize(sessions, Options));
}
