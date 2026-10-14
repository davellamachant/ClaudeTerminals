using System.Text.Json;

namespace ClaudeTerminals.Sessions;

internal sealed class SessionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Command { get; set; } = "claude";
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
