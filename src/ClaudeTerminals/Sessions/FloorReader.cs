using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeTerminals.Sessions;

/// <summary>
/// One thing that happened in a conversation, as the Floor plan tells it: a prompt from the user, a file read or
/// edited, a command, a test run, a question, an answer. <see cref="K"/> is the kind (prompt, read, search, edit, run,
/// build, test, ask, say, agent, compact, other); <see cref="File"/> the full path of the file for read and edit;
/// <see cref="Text"/> the prompt, the question, the first sentence of the answer or the command's description;
/// <see cref="Ok"/> whether a test, build or edit succeeded, once its result is known; <see cref="Q"/> whether an
/// answer ends in a question.
/// </summary>
internal sealed record FloorEvent(DateTime T, string K, string? File, string? Text, bool? Ok, bool Q = false)
{
    public bool? Ok { get; set; } = Ok;
}

/// <summary>The events of one conversation. <see cref="Version"/> is the count, so a page can ask for what's new.</summary>
internal sealed record FloorEvents(long Version, IReadOnlyList<FloorEvent> Events);

/// <summary>
/// Reads a conversation file into the events the Floor plan draws (see <see cref="FloorEvent"/>). Like
/// <see cref="ConversationReader"/>, it keeps what it has read and parses only the new lines next time. A tool's result
/// arrives in a later line than its call, so a test's or edit's outcome is filled in when the result is read.
/// </summary>
internal static partial class FloorReader
{
    private const int MaxText = 200;

    internal sealed class Cached
    {
        public readonly object Gate = new();
        public long Offset;
        public readonly List<FloorEvent> Events = [];
        /// <summary>Tool call id → the index of its event, until the result shows up.</summary>
        public readonly Dictionary<string, int> Pending = [];
    }

    private static readonly ConcurrentDictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static FloorEvents Read(string path)
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
                }
                if (stream.Length > c.Offset)
                {
                    stream.Seek(c.Offset, SeekOrigin.Begin);
                    c.Offset += ReadLines(stream, c);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep what was read before.
            }
            return new FloorEvents(c.Events.Count, c.Events.Select(e => e with { }).ToList());
        }
    }

    private static long ReadLines(Stream stream, Cached c)
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
                AddLine(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), c);
                buffer.SetLength(0);
                start = i + 1;
            }
            buffer.Write(chunk, start, n - start);
        }
        return consumed;
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
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Bool(e, "isSidechain")) return;
            if (!e.TryGetProperty("timestamp", out var ts) || !ts.TryGetDateTime(out var t)) return;
            t = t.ToUniversalTime();
            switch (Str(e, "type"))
            {
                case "system":
                    if (Str(e, "subtype") == "compact_boundary") c.Events.Add(new FloorEvent(t, "compact", null, null, null));
                    break;
                case "user":
                    if (Results(e, c)) break;
                    if (Bool(e, "isMeta") || Bool(e, "isCompactSummary")) break;
                    if (ConversationReader.PromptText(e) is { } prompt) c.Events.Add(new FloorEvent(t, "prompt", null, Clip(prompt), null));
                    break;
                case "assistant":
                    if (!e.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) break;
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
                    if (tools) break;
                    // An answer with no tool call: what Claude said, which may be a question.
                    var text = string.Join("\n", content.EnumerateArray().Where(p => Str(p, "type") == "text").Select(p => Str(p, "text") ?? "")).Trim();
                    if (text.Length > 0) c.Events.Add(new FloorEvent(t, "say", null, FirstSentence(text), null, text.EndsWith('?')));
                    break;
            }
        }
    }

    /// <summary>Fills in the outcome of pending calls from the tool results in a user line. True when the line held results.</summary>
    private static bool Results(JsonElement e, Cached c)
    {
        if (!e.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
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

    private static FloorEvent? ToolEvent(DateTime t, JsonElement part)
    {
        var name = Str(part, "name") ?? "";
        part.TryGetProperty("input", out var input);
        var file = Str(input, "file_path") ?? Str(input, "notebook_path");
        switch (name)
        {
            case "Read":
            case "Glob":
                return new FloorEvent(t, "read", file ?? Str(input, "path"), null, null);
            case "Grep":
                return new FloorEvent(t, "search", null, Clip(Str(input, "pattern") ?? ""), null);
            case "Edit":
            case "Write":
            case "NotebookEdit":
                return new FloorEvent(t, "edit", file, null, null);
            case "Bash":
            case "PowerShell":
            {
                var command = Str(input, "command") ?? "";
                var kind = TestCommand().IsMatch(command) ? "test" : BuildCommand().IsMatch(command) ? "build" : "run";
                var text = Str(input, "description") is { Length: > 0 } d ? d : command;
                return new FloorEvent(t, kind, null, Clip(text.ReplaceLineEndings(" ")), null);
            }
            case "AskUserQuestion":
            {
                string? question = null;
                if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
                    foreach (var q in qs.EnumerateArray()) { question = Str(q, "question"); break; }
                return new FloorEvent(t, "ask", null, Clip(question ?? "a question"), null, true);
            }
            case "Agent":
                return new FloorEvent(t, "agent", null, Clip(Str(input, "description") ?? "a helper"), null);
            default:
                return new FloorEvent(t, "other", null, name, null);
        }
    }

    /// <summary>The first sentence of an answer, without Markdown marks, for the one line the floor shows.</summary>
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
