using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace ClaudeTerminals.Sessions;

/// <summary>
/// Reads Codex's saved conversations: one rollout file each, ~/.codex/sessions/YYYY/MM/DD/rollout-&lt;time&gt;-&lt;id&gt;.jsonl,
/// starting with a session_meta line (id, cwd, when it started, what started it). Names given with /rename are in
/// ~/.codex/session_index.jsonl. Codex can't be told which conversation id to use, so a session learns it from the
/// rollout its Codex writes, and from the prompts in ~/.codex/history.jsonl (see <see cref="Live"/>).
/// </summary>
internal static class CodexConversations
{
    /// <summary>How far into a rollout to look for the first prompt: it comes after Codex's own (long) instructions.</summary>
    private const int HeadBytes = 512 * 1024;

    /// <param name="Interactive">
    /// A conversation a person had and can resume: not a codex exec run (the app's own commit messages among them), nor a
    /// thread Codex started under one (a Guardian review, a sub-agent), which names its parent and which codex resume refuses.
    /// </param>
    /// <param name="Parent">The conversation a thread Codex started under one belongs to (parent_thread_id).</param>
    private sealed record Meta(string Id, string? Cwd, DateTime Started, bool Interactive, string? Parent, string? FirstPrompt);

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, Meta? Meta)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static (DateTime Stamp, Dictionary<string, string> Names) names = (default, new());

    /// <summary>Codex's home folder: CODEX_HOME, or ~/.codex.</summary>
    internal static string Home
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : home;
        }
    }

    internal static string SessionsRoot => Path.Combine(Home, "sessions");

    /// <summary>Codex's saved conversations, latest first; only those started in <paramref name="directory"/> when one is given.</summary>
    public static List<ConversationInfo> List(string? directory, int max)
    {
        var normalizedDir = NormalizeDir(directory);
        var result = new List<ConversationInfo>();
        foreach (var file in Rollouts(SessionsRoot).OrderByDescending(f => f.LastWriteTimeUtc))
        {
            if (Read(file) is not { Interactive: true } meta) continue;
            if (normalizedDir != null && !SameDir(meta.Cwd, normalizedDir)) continue;
            result.Add(ToInfo(file, meta));
            if (result.Count >= max) break;
        }
        return result;
    }

    /// <summary>The saved conversation <see cref="Resumable"/> opens for <paramref name="id"/>, or null while Codex hasn't written it.</summary>
    public static ConversationInfo? Find(string id) =>
        Resumable(id) is { } open && FindRollout(open) is { } file && Read(file) is { } meta ? ToInfo(file, meta) : null;

    /// <summary>Whether Codex has saved the conversation <paramref name="id"/> and codex resume can open it.</summary>
    public static bool IsSaved(string id) => FindRollout(id) is { } file && Read(file) is { Interactive: true };

    /// <summary>
    /// The conversation codex resume opens for <paramref name="id"/>: itself, or for a thread Codex started under a
    /// conversation (which it won't resume), that conversation. Null when neither is saved.
    /// </summary>
    public static string? Resumable(string id)
    {
        for (var depth = 0; depth < 8 && FindRollout(id) is { } file && Read(file) is { } meta; depth++)
        {
            if (meta.Interactive) return meta.Id;
            if (meta.Parent is not { } parent) return null;
            id = parent;
        }
        return null;
    }

    /// <summary>
    /// The conversation the Codex started in <paramref name="directory"/> at <paramref name="startedUtc"/> is in now, of
    /// those since then: the latest interactive one started there (a new one after /new), or the latest one of that
    /// folder a prompt was sent in (one picked with /resume, which carries on in its old rollout), whichever came last.
    /// Never one another session is in (<paramref name="taken"/>). Null when there is none.
    /// </summary>
    public static string? Live(string directory, DateTime startedUtc, IReadOnlySet<string> taken)
    {
        var dir = NormalizeDir(directory);
        if (dir == null) return null;
        // A little slack: the session's clock starts just before Codex's own (and prompts' times are whole seconds).
        var since = startedUtc - TimeSpan.FromSeconds(2);
        var started = RecentRollouts(since)
            .Where(f => f.LastWriteTimeUtc >= since)
            .Select(Read)
            .Where(m => m is { Interactive: true } && m.Started >= since && SameDir(m.Cwd, dir))
            .Select(m => (m!.Id, At: m.Started));
        var prompted = RecentPrompts()
            .Where(p => p.At >= since && !taken.Contains(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Id: g.Key, At: g.Max(p => p.At)))
            .Where(p => FindRollout(p.Id) is { } file && Read(file) is { Interactive: true } m && SameDir(m.Cwd, dir));
        return started.Concat(prompted)
            .Where(c => !taken.Contains(c.Id))
            .OrderByDescending(c => c.At)
            .Select(c => c.Id)
            .FirstOrDefault();
    }

    /// <summary>A prompt typed into Codex: the conversation it was sent in, and when.</summary>
    private sealed record Prompt(string Id, DateTime At);

    private sealed record History(string Path, long Length, DateTime Stamp, List<Prompt> Prompts);

    private static History? history;

    /// <summary>How much of the end of history.jsonl to read: enough for the prompts of any session still running.</summary>
    private const int HistoryTailBytes = 256 * 1024;

    /// <summary>
    /// The latest prompts from history.jsonl, Codex's prompt history (one line per prompt: the conversation it went to
    /// and when), read again only once it has grown. Empty when Codex keeps no history (history.persistence = "none").
    /// </summary>
    private static List<Prompt> RecentPrompts()
    {
        var file = new FileInfo(Path.Combine(Home, "history.jsonl"));
        try
        {
            if (!file.Exists) return [];
            if (history is { } known && known.Path == file.FullName && known.Length == file.Length && known.Stamp == file.LastWriteTimeUtc) return known.Prompts;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - HistoryTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            if (start > 0) reader.ReadLine();   // the rest of a line cut off by the seek
            var prompts = new List<Prompt>();
            while (reader.ReadLine() is { } line)
                if (TryParse(line, out var e) && Str(e, "session_id") is { } id &&
                    e.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var seconds))
                    prompts.Add(new Prompt(id, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime));
            history = new History(file.FullName, file.Length, file.LastWriteTimeUtc, prompts);
            return prompts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }


    private static ConversationInfo ToInfo(FileInfo file, Meta meta)
    {
        var title = Names().GetValueOrDefault(meta.Id) ?? Truncate(meta.FirstPrompt, 120) ?? "(untitled)";
        return new ConversationInfo(meta.Id, meta.Cwd, null, title, Truncate(meta.FirstPrompt, 300), null, file.LastWriteTime,
            file.Length, meta.Started.ToLocalTime(), "codex");
    }

    /// <summary>
    /// The conversation <paramref name="id"/> as prompts and answers (for the rail and the Conversation tab), or null
    /// while Codex hasn't written it. A thread Codex started under a conversation shows that conversation.
    /// </summary>
    public static Conversation? Read(string id) =>
        Resumable(id) is { } open && FindRollout(open) is { } file ? ConversationReader.Read(open, file.FullName, CodexTranscript.Line) : null;

    /// <summary>Where each conversation's rollout is: the rail asks every few seconds, and finding one searches every day's folder.</summary>
    private static readonly ConcurrentDictionary<string, string> RolloutPaths = new(StringComparer.OrdinalIgnoreCase);

    private static FileInfo? FindRollout(string id)
    {
        if (!TerminalSession.IsConversationId(id)) return null;
        if (RolloutPaths.TryGetValue(id, out var known) && new FileInfo(known) is { Exists: true, Length: > 0 } file) return file;
        try
        {
            var root = SessionsRoot;
            if (!Directory.Exists(root)) return null;
            var found = Directory.EnumerateFiles(root, $"rollout-*-{id}.jsonl", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).FirstOrDefault(f => f.Length > 0);
            if (found != null) RolloutPaths[id] = found.FullName;
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<FileInfo> Rollouts(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "rollout-*.jsonl", SearchOption.AllDirectories).Select(f => new FileInfo(f)).Where(f => f.Length > 0).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Rollouts in the day folders from <paramref name="since"/> to today (local dates, as Codex names them).</summary>
    private static IEnumerable<FileInfo> RecentRollouts(DateTime since)
    {
        var day = since.ToLocalTime().Date.AddDays(-1);   // a rollout started just before midnight
        for (; day <= DateTime.Now.Date; day = day.AddDays(1))
            foreach (var f in Rollouts(Path.Combine(SessionsRoot, day.ToString("yyyy", CultureInfo.InvariantCulture),
                         day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture))))
                yield return f;
    }

    /// <summary>The session_meta line and the first prompt; cached until the file changes, and never again once the prompt is known.</summary>
    private static Meta? Read(FileInfo file)
    {
        if (Cache.TryGetValue(file.FullName, out var cached) && (cached.Stamp == file.LastWriteTimeUtc || cached.Meta?.FirstPrompt != null))
            return cached.Meta;
        Meta? meta = null;
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? id = null, cwd = null, parent = null, prompt = null;
            var interactive = true;
            var started = file.CreationTimeUtc;
            while (prompt == null && stream.Position < HeadBytes && reader.ReadLine() is { } line)
            {
                if (!TryParse(line, out var e) || !e.TryGetProperty("payload", out var p)) continue;
                switch (Str(e, "type"))
                {
                    case "session_meta" when id == null:
                        id = Str(p, "id");
                        cwd = Str(p, "cwd");
                        // A sub-agent's source is an object ({"subagent": …}); a person's is a name: "cli", "vscode", …
                        var source = p.TryGetProperty("source", out var s) ? s : default;
                        parent = Str(p, "parent_thread_id");
                        interactive = parent == null &&
                            (source.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
                             (source.ValueKind == JsonValueKind.String && source.GetString() != "exec"));
                        if (Str(p, "timestamp") is { } ts && DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                            started = at;
                        break;
                    case "response_item" when Str(p, "type") == "message" && Str(p, "role") == "user":
                        prompt = CodexTranscript.PromptText(p);
                        break;
                }
            }
            if (id != null) meta = new Meta(id, cwd, started, interactive, parent, prompt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        Cache[file.FullName] = (file.LastWriteTimeUtc, meta);
        return meta;
    }

    /// <summary>Thread names from session_index.jsonl (a later line renames).</summary>
    private static Dictionary<string, string> Names()
    {
        var file = new FileInfo(Path.Combine(Home, "session_index.jsonl"));
        try
        {
            if (!file.Exists) return [];
            var current = names;
            if (current.Stamp == file.LastWriteTimeUtc) return current.Names;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                if (TryParse(line, out var e) && Str(e, "id") is { } id && Str(e, "thread_name") is { Length: > 0 } name)
                    map[id] = name;
            names = (file.LastWriteTimeUtc, map);
            return map;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? NormalizeDir(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try { return Path.GetFullPath(directory).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool SameDir(string? cwd, string normalizedDir) =>
        cwd != null && string.Equals(cwd.TrimEnd('\\'), normalizedDir, StringComparison.OrdinalIgnoreCase);

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

    private static string? Truncate(string? s, int max)
    {
        if (s == null) return null;
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
