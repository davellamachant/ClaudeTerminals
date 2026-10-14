using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeTerminals.Sessions;

internal sealed record ConversationInfo(
    string SessionId, string? Cwd, string? Branch, string Title, string? FirstPrompt, string? LastPrompt, DateTime Modified, long Size);

/// <summary>
/// Reads Claude Code's saved conversations (~/.claude/projects/&lt;encoded-cwd&gt;/&lt;sessionId&gt;.jsonl).
/// Transcripts can be hundreds of MB, so only the head (cwd, first prompt) and tail (latest title) are read.
/// </summary>
internal static partial class ConversationIndex
{
    private const int HeadBytes = 96 * 1024;
    private const int TailBytes = 128 * 1024;

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, ConversationInfo Info)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static string ProjectsRoot
    {
        get
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDir))
                configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(configDir, "projects");
        }
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    /// <summary>Claude names each project folder after its cwd with every non-alphanumeric character replaced by '-'.</summary>
    private static string Encode(string directory) => NonAlphanumeric().Replace(Path.GetFullPath(directory).TrimEnd('\\'), "-");

    public static List<ConversationInfo> List(string? directory, int max)
    {
        var root = ProjectsRoot;
        if (!Directory.Exists(root)) return [];

        IEnumerable<string> projectDirs = Directory.EnumerateDirectories(root);
        string? normalizedDir = null;
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            var encoded = Encode(directory);
            normalizedDir = Path.GetFullPath(directory).TrimEnd('\\');
            projectDirs = projectDirs.Where(d => string.Equals(Path.GetFileName(d), encoded, StringComparison.OrdinalIgnoreCase));
        }

        var files = projectDirs
            .SelectMany(d => SafeEnumerate(d))
            .Where(f => !Path.GetFileName(f).Contains(".orphaned", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .Where(f => f.Length > 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(max * 2)   // some files turn out to be empty shells; over-fetch a little
            .ToList();

        var result = new List<ConversationInfo>();
        foreach (var file in files)
        {
            var info = Read(file);
            if (info == null) continue;
            // Different cwds can encode to the same folder name; confirm the real path when filtering.
            if (normalizedDir != null && info.Cwd != null &&
                !string.Equals(info.Cwd.TrimEnd('\\'), normalizedDir, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(info);
            if (result.Count >= max) break;
        }
        return result;
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static ConversationInfo? Read(FileInfo file)
    {
        if (Cache.TryGetValue(file.FullName, out var cached) && cached.Stamp == file.LastWriteTimeUtc)
            return cached.Info;

        string? cwd = null, branch = null, firstPrompt = null, sessionId = null;
        string? aiTitle = null, customTitle = null, lastPrompt = null;
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            foreach (var line in ReadLines(stream, 0, HeadBytes, skipFirstPartial: false))
            {
                if (!TryParse(line, out var e)) continue;
                sessionId ??= Str(e, "sessionId");
                cwd ??= Str(e, "cwd");
                if (branch == null && Str(e, "gitBranch") is { } b && b != "HEAD") branch = b;
                if (firstPrompt == null && Str(e, "type") == "user" && !Bool(e, "isMeta") && !Bool(e, "isSidechain"))
                    firstPrompt = UserText(e);
                CollectTitles(e, ref aiTitle, ref customTitle, ref lastPrompt);
                if (cwd != null && firstPrompt != null && branch != null) break;
            }

            if (file.Length > HeadBytes)
            {
                var start = Math.Max(HeadBytes, file.Length - TailBytes);
                foreach (var line in ReadLines(stream, start, (int)(file.Length - start), skipFirstPartial: true))
                    if (TryParse(line, out var e)) CollectTitles(e, ref aiTitle, ref customTitle, ref lastPrompt);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        sessionId ??= Path.GetFileNameWithoutExtension(file.Name);
        if (firstPrompt == null && aiTitle == null && customTitle == null && lastPrompt == null) return null;

        var title = customTitle ?? aiTitle ?? Truncate(firstPrompt ?? lastPrompt, 120) ?? "(untitled)";
        var info = new ConversationInfo(sessionId, cwd, branch, title, Truncate(firstPrompt, 300), Truncate(lastPrompt, 300), file.LastWriteTime, file.Length);
        Cache[file.FullName] = (file.LastWriteTimeUtc, info);
        return info;
    }

    private static void CollectTitles(JsonElement e, ref string? aiTitle, ref string? customTitle, ref string? lastPrompt)
    {
        switch (Str(e, "type"))
        {
            case "ai-title": aiTitle = Str(e, "aiTitle") ?? aiTitle; break;
            case "custom-title": customTitle = Str(e, "customTitle") ?? customTitle; break;
            case "last-prompt": lastPrompt = Str(e, "lastPrompt") ?? lastPrompt; break;
        }
    }

    private static string? UserText(JsonElement e)
    {
        if (!e.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c)) return null;
        string? text = null;
        if (c.ValueKind == JsonValueKind.String) text = c.GetString();
        else if (c.ValueKind == JsonValueKind.Array)
            foreach (var part in c.EnumerateArray())
                if (Str(part, "type") == "text") { text = Str(part, "text"); break; }
        text = text?.Trim();
        // Slash-command echoes, tool results and caveats are wrapped in XML-ish tags; they don't describe the conversation.
        return string.IsNullOrEmpty(text) || text.StartsWith('<') ? null : text;
    }

    private static IEnumerable<string> ReadLines(FileStream stream, long offset, int count, bool skipFirstPartial)
    {
        var buffer = new byte[count];
        stream.Seek(offset, SeekOrigin.Begin);
        var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var lines = text.Split('\n');
        // The first line of a tail read and the last line of any read may be cut in half.
        var from = skipFirstPartial ? 1 : 0;
        for (var i = from; i < lines.Length - 1; i++) yield return lines[i];
        if (offset + read >= stream.Length && lines.Length > from) yield return lines[^1];
    }

    private static bool TryParse(string line, out JsonElement element)
    {
        element = default;
        line = line.Trim();
        if (line.Length < 2 || line[0] != '{') return false;
        try
        {
            element = JsonDocument.Parse(line).RootElement;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Truncate(string? s, int max)
    {
        if (s == null) return null;
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
