using System.Text.Json;

namespace ClaudeTerminals.Sessions;

internal sealed class SessionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Command { get; set; } = "claude";
    /// <summary>Id of the project this session belongs to, or null for none (shown whichever projects are selected).</summary>
    public string? Project { get; set; }
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
        try
        {
            if (File.Exists(path))
            {
                var projects = (JsonSerializer.Deserialize<List<ProjectInfo>>(File.ReadAllText(path)) ?? [])
                    .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                    .DistinctBy(p => p.Id)
                    .ToList();
                // The page puts the colour straight into a style, so only a plain #rrggbb ever reaches it.
                foreach (var p in projects) p.Color = ProjectInfo.NormalizeColor(p.Color);
                return projects;
            }
        }
        catch (Exception)
        {
            // A corrupt file loses the grouping, never the sessions themselves.
        }
        return [];
    }

    public static void Save(IEnumerable<ProjectInfo> projects) => Save(FilePath, projects);

    internal static void Save(string path, IEnumerable<ProjectInfo> projects)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(projects, Options));
        File.Move(temp, path, true);
    }
}

/// <summary>
/// Persists the configured sessions to %APPDATA%\ClaudeTerminals\sessions.json
/// (or the folder named by CLAUDE_TERMINALS_DATA, for separate profiles/testing).
/// </summary>
internal static class SessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string AppDataFolder { get; } =
        Environment.GetEnvironmentVariable("CLAUDE_TERMINALS_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeTerminals");

    private static string FilePath => Path.Combine(AppDataFolder, "sessions.json");

    public static List<SessionInfo> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<SessionInfo>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception)
        {
            // A corrupt file should not prevent the app from starting.
        }
        return [];
    }

    public static void Save(IEnumerable<SessionInfo> sessions)
    {
        System.IO.Directory.CreateDirectory(AppDataFolder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(sessions, Options));
        File.Move(temp, FilePath, true);
    }
}
