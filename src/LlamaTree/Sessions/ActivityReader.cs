using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaTree.Sessions;

/// <summary>
/// One thing that happened in a conversation, as plugins with access to sessions are told it (see PluginCatalog): a prompt from the user, a file read or
/// edited, a command, a test run, a question, an answer. <see cref="K"/> is the kind (prompt, read, search, edit, run,
/// build, test, ask, say, agent, compact, other); <see cref="File"/> the full path of the file for read and edit;
/// <see cref="Text"/> the prompt, the question, the first sentence of the answer or the command's description;
/// <see cref="Ok"/> whether a test, build or edit succeeded, once its result is known; <see cref="Q"/> whether an
/// answer ends in a question. The first event of each reply carries what the reply cost: <see cref="Ctx"/>, the tokens
/// the agent was sent (its context: the prompt, cached or not), and <see cref="Out"/>, the tokens it wrote.
/// </summary>
internal sealed record ActivityEvent(DateTime T, string K, string? File, string? Text, bool? Ok, bool Q = false)
{
    public bool? Ok { get; set; } = Ok;
    public int? Ctx { get; set; }
    public int? Out { get; set; }
}

/// <summary>The events of one conversation. <see cref="Version"/> is the count, so a plugin can ask for what's new.</summary>
internal sealed record ActivityEvents(long Version, IReadOnlyList<ActivityEvent> Events);

/// <summary>
/// Reads a conversation file into its events (see <see cref="ActivityEvent"/>). Like
/// <see cref="ConversationReader"/>, it keeps what it has read and parses only the new lines next time. A tool's result
/// arrives in a later line than its call, so a test's or edit's outcome is filled in when the result is read.
/// </summary>
internal static partial class ActivityReader
{
    private const int MaxText = 200;

    internal sealed class Cached
    {
        public readonly object Gate = new();
        public long Offset;
        public readonly List<ActivityEvent> Events = [];
        /// <summary>Tool call id → the index of its event, until the result shows up.</summary>
        public readonly Dictionary<string, int> Pending = [];
        /// <summary>The reply the last assistant line belonged to, and the index of the event carrying its cost (-1 while it has none).</summary>
        public string? LastReply;
        public int CostAt = -1;
    }

    private static readonly ConcurrentDictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ActivityEvents Read(string path)
    {
        var c = Cache.GetOrAdd(path, _ => new Cached());
        lock (c.Gate)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < c.Offset)
                {
                    c.Offset = 0;
                    c.Events.Clear();
                    c.Pending.Clear();
                    c.LastReply = null;
                    c.CostAt = -1;
                }
                if (stream.Length > c.Offset)
                {
                    stream.Seek(c.Offset, SeekOrigin.Begin);
                    ConversationReader.ReadLines(stream, ref c.Offset, line => AddLine(line, c));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep what was read before.
            }
            return new ActivityEvents(c.Events.Count, c.Events.Select(e => e with { }).ToList());
        }
    }

    internal static void AddLine(ReadOnlySpan<byte> line, Cached c)
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
            try
            {
                AddLine(doc.RootElement, c);
            }
            catch (Exception ex) when (ConversationReader.IsMalformedLine(ex))
            {
                // A line in a shape this doesn't expect: skip it and keep the rest.
            }
        }
    }

    private static void AddLine(JsonElement e, Cached c)
    {
        if (e.ValueKind != JsonValueKind.Object || Bool(e, "isSidechain")) return;
        if (ConversationReader.Time(e, "timestamp") is not { } t) return;
        t = t.ToUniversalTime();
        switch (Str(e, "type"))
        {
            case "system":
                if (Str(e, "subtype") == "compact_boundary") c.Events.Add(new ActivityEvent(t, "compact", null, null, null));
                break;
            case "user":
                if (Results(e, c)) break;
                if (Bool(e, "isMeta") || Bool(e, "isCompactSummary")) break;
                if (ConversationReader.PromptText(e) is { } prompt) c.Events.Add(new ActivityEvent(t, "prompt", null, Clip(prompt), null));
                break;
            case "assistant":
                if (!ConversationReader.Obj(e, "message", out var m) || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) break;
                var first = c.Events.Count;
                var tools = false;
                foreach (var part in content.EnumerateArray())
                    if (Str(part, "type") == "tool_use")
                    {
                        tools = true;
                        var ev = ToolEvent(t, part);
                        if (ev == null) continue;
                        c.Events.Add(ev);
                        if (ev.K is "edit" or "run" or "build" or "test" && Str(part, "id") is { } id) c.Pending[id] = c.Events.Count - 1;
                    }
                if (!tools)
                {
                    // An answer with no tool call: what Claude said, which may be a question.
                    var text = string.Join("\n", content.EnumerateArray().Where(p => Str(p, "type") == "text").Select(p => Str(p, "text") ?? "")).Trim();
                    if (text.Length > 0) c.Events.Add(new ActivityEvent(t, "say", null, FirstSentence(text), null, text.EndsWith('?')));
                }
                Cost(m, c, first);
                break;
        }
    }

    /// <summary>
    /// Puts a reply's token counts on its first event. Claude Code writes one line per block of a reply, each with the
    /// reply's usage so far, so lines of the same reply (message id) update the one event rather than add up.
    /// </summary>
    private static void Cost(JsonElement m, Cached c, int first)
    {
        if (!m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        var ctx = Int(u, "input_tokens") + Int(u, "cache_creation_input_tokens") + Int(u, "cache_read_input_tokens");
        var output = Int(u, "output_tokens");
        var reply = Str(m, "id");
        if (reply != null && reply == c.LastReply && c.CostAt >= 0)
        {
            var ev = c.Events[c.CostAt];
            ev.Ctx = Math.Max(ev.Ctx ?? 0, ctx);
            ev.Out = Math.Max(ev.Out ?? 0, output);
            return;
        }
        c.LastReply = reply;
        c.CostAt = c.Events.Count > first ? first : -1;
        if (c.CostAt >= 0)
        {
            c.Events[c.CostAt].Ctx = ctx;
            c.Events[c.CostAt].Out = output;
        }
    }

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>Fills in the outcome of pending calls from the tool results in a user line. True when the line held results.</summary>
    private static bool Results(JsonElement e, Cached c)
    {
        if (!ConversationReader.Obj(e, "message", out var m) || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
        var any = false;
        foreach (var part in content.EnumerateArray())
        {
            if (Str(part, "type") != "tool_result") continue;
            any = true;
            if (Str(part, "tool_use_id") is not { } id || !c.Pending.Remove(id, out var index)) continue;
            var ev = c.Events[index];
            var text = ResultText(part);
            ev.Ok = ev.K == "edit" ? !EditFailed().IsMatch(text) : Bool(part, "is_error") || Failed().IsMatch(text) ? false : Passed().IsMatch(text) ? true : null;
        }
        return any;
    }

    private static string ResultText(JsonElement part)
    {
        if (!part.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", content.EnumerateArray().Select(p => Str(p, "text") ?? ""));
    }

    private static ActivityEvent? ToolEvent(DateTime t, JsonElement part)
    {
        var name = Str(part, "name") ?? "";
        part.TryGetProperty("input", out var input);
        var file = Str(input, "file_path") ?? Str(input, "notebook_path");
        switch (name)
        {
            case "Read":
            case "Glob":
                return new ActivityEvent(t, "read", file ?? Str(input, "path"), null, null);
            case "Grep":
                return new ActivityEvent(t, "search", null, Clip(Str(input, "pattern") ?? ""), null);
            case "Edit":
            case "Write":
            case "NotebookEdit":
                return new ActivityEvent(t, "edit", file, null, null);
            case "Bash":
            case "PowerShell":
            {
                var command = Str(input, "command") ?? "";
                var kind = TestCommand().IsMatch(command) ? "test" : BuildCommand().IsMatch(command) ? "build" : "run";
                var text = Str(input, "description") is { Length: > 0 } d ? d : command;
                return new ActivityEvent(t, kind, null, Clip(text.ReplaceLineEndings(" ")), null);
            }
            case "AskUserQuestion":
            {
                string? question = null;
                if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
                    foreach (var q in qs.EnumerateArray()) { question = Str(q, "question"); break; }
                return new ActivityEvent(t, "ask", null, Clip(question ?? "a question"), null, true);
            }
            case "Agent":
                return new ActivityEvent(t, "agent", null, Clip(Str(input, "description") ?? "a helper"), null);
            default:
                return new ActivityEvent(t, "other", null, name, null);
        }
    }

    /// <summary>The first sentence of an answer, without Markdown marks, for the one line a plugin shows.</summary>
    internal static string FirstSentence(string text)
    {
        var s = Marks().Replace(text, "");
        s = Spaces().Replace(s, " ").Trim();
        var m = Sentence().Match(s);
        if (m.Success) return m.Groups[1].Value;
        return s.Length <= 160 ? s : s[..160] + "…";
    }

    private static string Clip(string s) => s.Length <= MaxText ? s : s[..MaxText] + "…";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"dotnet\s+test|npm\s+test|npx\s+vitest|\bvitest\b|\bpytest\b|\bjest\b|go\s+test|cargo\s+test", RegexOptions.IgnoreCase)]
    private static partial Regex TestCommand();

    [GeneratedRegex(@"dotnet\s+(build|publish)|npm\s+run\s+build|\bmsbuild\b|cargo\s+build|go\s+build", RegexOptions.IgnoreCase)]
    private static partial Regex BuildCommand();

    [GeneratedRegex(@"Failed!|Build FAILED|error CS\d|\bFAILED\b|\d+ failed|Exit code [1-9]|Tests failed", RegexOptions.IgnoreCase)]
    private static partial Regex Failed();

    [GeneratedRegex(@"Passed!|Build succeeded|\d+ passed|Tests passed|\bok\b", RegexOptions.IgnoreCase)]
    private static partial Regex Passed();

    [GeneratedRegex(@"^\s*(<tool_use_error>|Error|File has not been read|.*old_string.*(not found|does not appear))", RegexOptions.IgnoreCase)]
    private static partial Regex EditFailed();

    [GeneratedRegex(@"[*_`#>]")]
    private static partial Regex Marks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(.{20,160}?[.!?])(\s|$)")]
    private static partial Regex Sentence();
}
