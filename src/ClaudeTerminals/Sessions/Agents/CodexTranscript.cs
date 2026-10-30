using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeTerminals.Sessions;

/// <summary>
/// Reads a Codex rollout into prompts and answers for the conversation rail and Conversation tab (see
/// <see cref="ConversationReader.Read"/>). The conversation is in its response_item lines: the person's messages, Codex's
/// messages and its tool calls. Codex also sends context as user messages (the environment, open pages, replies to its
/// questions), wrapped in tags, and those aren't prompts.
/// </summary>
internal static partial class CodexTranscript
{
    /// <summary>One rollout line (a <see cref="TranscriptParser"/>).</summary>
    public static void Line(JsonElement e, List<ConversationTurn> turns)
    {
        if (Str(e, "type") != "response_item" || !e.TryGetProperty("payload", out var p)) return;
        switch (Str(p, "type"))
        {
            case "message" when Str(p, "role") == "user":
                if (PromptText(p) is { } prompt)
                    turns.Add(new ConversationTurn
                    {
                        Index = turns.Count,
                        Prompt = ConversationReader.Clip(prompt, ConversationReader.MaxTextChars),
                        Time = e.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var t) ? t : null,
                    });
                break;
            case "message" when Str(p, "role") == "assistant" && turns.Count > 0:
                if (!p.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
                foreach (var part in content.EnumerateArray())
                    if (Str(part, "type") == "output_text" && Str(part, "text") is { } text && !string.IsNullOrWhiteSpace(text))
                        turns[^1].Items.Add(new ConversationItem("text", ConversationReader.Clip(text.Trim(), ConversationReader.MaxTextChars)));
                break;
            case "function_call" when turns.Count > 0:
                turns[^1].Items.Add(Tool(Str(p, "name"), ArgumentsDetail(Str(p, "arguments"))));
                break;
            case "custom_tool_call" when turns.Count > 0:
                turns[^1].Items.Add(Tool(Str(p, "name"), InputDetail(Str(p, "input"))));
                break;
            case "local_shell_call" when turns.Count > 0:
                var command = p.TryGetProperty("action", out var action) && action.TryGetProperty("command", out var c) ? Words(c) : null;
                turns[^1].Items.Add(Tool("shell", command));
                break;
        }
    }

    /// <summary>What the person typed in a user message, or null when it is only context Codex added.</summary>
    internal static string? PromptText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) return null;
        var parts = new List<string>();
        foreach (var part in c.EnumerateArray())
            if (Str(part, "type") == "input_text" && Str(part, "text")?.Trim() is { Length: > 0 } text && !text.StartsWith('<'))
                parts.Add(text);
        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    private static ConversationItem Tool(string? name, string? detail) =>
        new("tool", ConversationReader.Clip((detail ?? "").ReplaceLineEndings(" ").Trim(), ConversationReader.MaxToolChars), name ?? "tool");

    private static readonly string[] ArgumentKeys = ["cmd", "command", "file_path", "path", "pattern", "url", "query", "task_name", "target"];

    /// <summary>The telling argument of a function call (its arguments are a JSON object in a string).</summary>
    private static string? ArgumentsDetail(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in ArgumentKeys)
                if (doc.RootElement.TryGetProperty(key, out var v) && Words(v) is { Length: > 0 } words)
                    return words;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The telling part of a custom tool's input: the files a patch touches, the command a script runs, or else its
    /// first line.
    /// </summary>
    private static string? InputDetail(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        // A patch is plain text from apply_patch, or a JSON-escaped string when a script applies it.
        var files = PatchFile().Matches(input).Select(m => m.Groups[1].Value.Replace(@"\\", @"\").Trim()).Distinct().ToList();
        if (files.Count > 0) return string.Join(", ", files);
        if (ScriptCommand().Match(input) is { Success: true } cmd)
        {
            try { return JsonSerializer.Deserialize<string>("\"" + cmd.Groups[1].Value + "\""); }
            catch (JsonException) { return cmd.Groups[1].Value; }
        }
        return input.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("//"));
    }

    [GeneratedRegex(@"\*\*\* (?:Add|Update|Delete) File: (.+?)\s*(?:\\n|\r?\n|""|$)", RegexOptions.Multiline)]
    private static partial Regex PatchFile();

    [GeneratedRegex(@"\bcmd\s*:\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex ScriptCommand();

    /// <summary>A string, or an array of strings joined with spaces (a command and its arguments).</summary>
    private static string? Words(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Array => string.Join(" ", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
        _ => null,
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
