using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeTerminals.Sessions;

/// <summary>One part of Claude's answer: a block of Markdown text, or a one-line summary of a tool call.</summary>
internal sealed record ConversationItem(string Kind, string Text, string? Tool = null);

/// <summary>A prompt the user sent and everything Claude answered until the next one.</summary>
internal sealed class ConversationTurn
{
    public required int Index { get; init; }
    public required string Prompt { get; init; }
    public DateTime? Time { get; init; }
    public List<ConversationItem> Items { get; } = [];

    /// <summary>A copy that later reads won't append to.</summary>
    public ConversationTurn Snapshot()
    {
        var copy = new ConversationTurn { Index = Index, Prompt = Prompt, Time = Time };
        copy.Items.AddRange(Items);
        return copy;
    }
}

/// <summary>A parsed conversation file. <see cref="Version"/> changes whenever a prompt or answer is added.</summary>
internal sealed record Conversation(string SessionId, string Path, long Version, IReadOnlyList<ConversationTurn> Turns);

/// <summary>Adds what one line of an agent's transcript says to the turns: a new prompt, or items of the answer to the last one.</summary>
internal delegate void TranscriptParser(JsonElement line, List<ConversationTurn> turns);

/// <summary>
/// Finds the conversation a session's terminal is showing and reads it into prompts and answers.
/// Claude Code writes ~/.claude/sessions/&lt;pid&gt;.json for each interactive process, naming the conversation it is in;
/// the conversation itself is an append-only JSONL file, so after the first read only the new lines are parsed.
/// <see cref="Read"/> does that for any agent whose transcript is JSONL, given its <see cref="TranscriptParser"/>.
/// </summary>
internal static partial class ConversationReader
{
    internal const int MaxTextChars = 100_000;
    internal const int MaxToolChars = 200;

    private sealed class Cached
    {
        public readonly object Gate = new();
        public long Offset;
        public readonly List<ConversationTurn> Turns = [];
    }

    private static readonly ConcurrentDictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The conversation to reopen with --resume after a restart, or null. Only one Claude has saved: before the first
    /// prompt there's a session id but no conversation, and resuming it fails ("No conversation found").
    /// </summary>
    public static string? ResumableConversation(IEnumerable<int> processIds) =>
        FindLiveSession(processIds) is { } live && FindTranscript(live.SessionId, live.Cwd) != null ? live.SessionId : null;

    /// <summary>Whether Claude has saved the conversation <paramref name="sessionId"/> (so --resume can open it).</summary>
    public static bool IsSaved(string sessionId, string? directory) => FindTranscript(sessionId, directory) != null;

    /// <summary>The conversation of the Claude process among <paramref name="processIds"/>, or null when there is none.</summary>

    public static Conversation? ForProcesses(IEnumerable<int> processIds)
    {
        var live = FindLiveSession(processIds);
        if (live == null) return null;
        var path = FindTranscript(live.Value.SessionId, live.Value.Cwd);
        return path == null ? null : Read(live.Value.SessionId, path);
    }

    /// <summary>
    /// Reads ~/.claude/sessions/&lt;pid&gt;.json for each process. A Claude started from inside the session (a nested
    /// claude run by a tool) has its own file, so the earliest-started interactive one is the session's own.
    /// </summary>
    internal static (string SessionId, string? Cwd)? FindLiveSession(IEnumerable<int> processIds)
    {
        var dir = Path.Combine(ConversationIndex.ConfigDir, "sessions");
        (string SessionId, string? Cwd, long Start, bool Interactive)? best = null;
        foreach (var pid in processIds)
        {
            var file = Path.Combine(dir, $"{pid}.json");
            JsonElement e;
            try
            {
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                e = doc.RootElement.Clone();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
            if (Str(e, "sessionId") is not { } sessionId) continue;
            // Process ids are reused: the file must belong to this process, not an earlier one with the same id.
            var procStart = Int64(e, "procStart");
            if (procStart != 0 && !StartedAt(pid, procStart)) continue;
            var interactive = Str(e, "kind") is null or "interactive";
            var candidate = (sessionId, Str(e, "cwd"), procStart, interactive);
            if (best == null || (interactive && !best.Value.Interactive) ||
                (interactive == best.Value.Interactive && procStart < best.Value.Start))
                best = candidate;
        }
        return best == null ? null : (best.Value.SessionId, best.Value.Cwd);
    }

    /// <summary>A whole number written as a JSON number or, as Claude Code writes procStart (too big for JavaScript), a string; 0 otherwise.</summary>
    private static long Int64(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
            _ => 0,
        };
    }

    /// <summary>procStart is the process creation time as a FILETIME.</summary>
    private static bool StartedAt(int pid, long procStart)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return Math.Abs(p.StartTime.ToUniversalTime().ToFileTimeUtc() - procStart) < TimeSpan.TicksPerSecond;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string? FindTranscript(string sessionId, string? cwd)
    {
        var root = ConversationIndex.ProjectsRoot;
        var name = sessionId + ".jsonl";
        if (cwd != null)
        {
            var direct = Path.Combine(root, ConversationIndex.Encode(cwd), name);
            if (File.Exists(direct)) return direct;
        }
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <param name="parse">How the agent's lines read; Claude's by default.</param>
    internal static Conversation Read(string sessionId, string path, TranscriptParser? parse = null)
    {
        var c = Cache.GetOrAdd(path, _ => new Cached());
        lock (c.Gate)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < c.Offset)
                {
                    // Rewritten rather than appended to: start over.
                    c.Offset = 0;
                    c.Turns.Clear();
                }
                if (stream.Length > c.Offset)
                {
                    stream.Seek(c.Offset, SeekOrigin.Begin);
                    c.Offset += ReadLines(stream, c.Turns, parse ?? ClaudeLine);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep what was read before.
            }
            // Only the last turn grows, and only by whole items, so the counts identify the content.
            var version = c.Turns.Count == 0 ? 0 : ((long)c.Turns.Count << 32) | (uint)c.Turns[^1].Items.Count;
            return new Conversation(sessionId, path, version, c.Turns.Select(t => t.Snapshot()).ToList());
        }
    }

    /// <summary>Parses complete lines from the stream into the turns. Returns the bytes consumed (a half-written last line is left for next time).</summary>
    private static long ReadLines(Stream stream, List<ConversationTurn> turns, TranscriptParser parse)
    {
        long consumed = 0;
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < n; i++)
            {
                if (chunk[i] != (byte)'\n') continue;
                buffer.Write(chunk, start, i - start);
                consumed += buffer.Length + 1;
                AddLine(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), turns, parse);
                buffer.SetLength(0);
                start = i + 1;
            }
            buffer.Write(chunk, start, n - start);
        }
        return consumed;
    }

    private static void AddLine(ReadOnlySpan<byte> line, List<ConversationTurn> turns, TranscriptParser parse)
    {
        if (line.Length < 2) return;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line.ToArray());
        }
        catch (JsonException)
        {
            return;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object) parse(doc.RootElement, turns);
        }
    }

    /// <summary>A line of Claude Code's transcript.</summary>
    private static void ClaudeLine(JsonElement e, List<ConversationTurn> turns)
    {
        if (Bool(e, "isSidechain")) return;
        switch (Str(e, "type"))
        {
            case "user":
                if (Bool(e, "isMeta") || Bool(e, "isCompactSummary")) return;
                if (PromptText(e) is { } prompt)
                    turns.Add(new ConversationTurn
                    {
                        Index = turns.Count,
                        Prompt = Clip(prompt, MaxTextChars),
                        Time = e.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var t) ? t : null,
                    });
                break;
            case "assistant":
                if (turns.Count == 0 || !e.TryGetProperty("message", out var m) ||
                    !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
                var items = turns[^1].Items;
                foreach (var part in content.EnumerateArray())
                {
                    switch (Str(part, "type"))
                    {
                        case "text" when Str(part, "text") is { } text && !string.IsNullOrWhiteSpace(text):
                            items.Add(new ConversationItem("text", Clip(text.Trim(), MaxTextChars)));
                            break;
                        case "tool_use":
                            items.Add(new ConversationItem("tool", ToolDetail(part), Str(part, "name") ?? "tool"));
                            break;
                    }
                }
                break;
        }
    }

    [GeneratedRegex(@"<command-name>\s*(.*?)\s*</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandName();

    [GeneratedRegex(@"<command-args>\s*(.*?)\s*</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgs();

    [GeneratedRegex(@"<bash-input>\s*(.*?)\s*</bash-input>", RegexOptions.Singleline)]
    private static partial Regex BashInput();

    /// <summary>The text the user typed, or null for tool results, notifications and other entries that aren't prompts.</summary>
    internal static string? PromptText(JsonElement e)
    {
        if (!e.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c)) return null;
        string? text;
        if (c.ValueKind == JsonValueKind.String) text = c.GetString();
        else if (c.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in c.EnumerateArray())
            {
                var type = Str(part, "type");
                if (type == "tool_result") return null;
                // Pasted images appear as [Image #1] in the typed text, plus a separate "[Image: source: …]" note.
                if (type == "text" && Str(part, "text") is { } t && !t.StartsWith("[Image: ", StringComparison.Ordinal)) parts.Add(t);
            }
            text = string.Join("\n", parts);
        }
        else return null;

        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.StartsWith("[Request interrupted", StringComparison.Ordinal)) return null;
        if (!text.StartsWith('<')) return text;

        // Slash commands and ! commands are wrapped in tags; their output and notifications aren't prompts.
        if (CommandName().Match(text) is { Success: true } name)
        {
            var args = CommandArgs().Match(text) is { Success: true } a ? a.Groups[1].Value : "";
            return (name.Groups[1].Value + " " + args).Trim();
        }
        if (BashInput().Match(text) is { Success: true } bash) return "! " + bash.Groups[1].Value;
        return null;
    }

    private static readonly string[] ToolKeys = ["command", "file_path", "notebook_path", "pattern", "url", "query", "path", "skill", "description", "prompt"];

    private static string ToolDetail(JsonElement part)
    {
        if (!part.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in ToolKeys)
            if (Str(input, key) is { } v && !string.IsNullOrWhiteSpace(v))
                return Clip(v.ReplaceLineEndings(" ").Trim(), MaxToolChars);
        return "";
    }

    internal static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
