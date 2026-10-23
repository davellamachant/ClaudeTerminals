using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeTerminals.Panels;

/// <summary>
/// The MCP side of panels: answers one JSON-RPC message from a session's Claude (initialize, tools/list, tools/call).
/// Nothing here knows about any tracker or service; Claude decides what goes in a panel.
/// </summary>
internal sealed class PanelMcp(PanelStore store)
{
    public const string ServerName = "claude-terminals";

    private static readonly JsonSerializerOptions GetOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement.Clone();

    private static readonly string[] ProtocolVersions =["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>A session's Claude connected (raised on the server's thread).</summary>
    public event Action<string>? Initialized;

    private const string Instructions =
        "This session runs inside Claude Terminals, which can show panels beside the terminal (as tabs next to Git Changes). " +
        "Use show_panel when the person asks to see something in a panel, or when a skill or command says to; not for ordinary answers. " +
        "A panel is Markdown (each heading folds) or a list of items with badges, a link and buttons, optionally in collapsible groups.Buttons only carry a prompt: the person sends it. " +
        "Panels stay after Claude Terminals restarts until they are closed; list_panels shows this session's panels.";

    /// <summary>The reply to <paramref name="message"/>, or null for a notification (which gets none).</summary>
    public JsonObject? Handle(string session, JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String)
            return message.ValueKind == JsonValueKind.Object && message.TryGetProperty("id", out var badId)
                ? Error(JsonNode.Parse(badId.GetRawText()), -32600, "Invalid request")
                : null;   // a response to something we never asked, or junk: nothing to say
        if (!message.TryGetProperty("id", out var idElement)) return null;   // notifications/initialized and the like
        var id = JsonNode.Parse(idElement.GetRawText());
        var p = message.TryGetProperty("params", out var pe) ? pe : default;

        switch (m.GetString())
        {
            case "initialize":
            {
                var asked = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var v) ? v.GetString() : null;
                Initialized?.Invoke(session);
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[1],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = Updates.UpdateService.CurrentVersion.ToString() },
                    ["instructions"] = Instructions,
                });
            }
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = JsonNode.Parse(ToolsJson) });
            case "tools/call":
            {
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    return Error(id, -32602, "tools/call needs a tool name");
                var args = p.TryGetProperty("arguments", out var a) && a.ValueKind != JsonValueKind.Null ? a : NoArguments;
                try
                {
                    return Result(id, ToolResult(Call(session, name.GetString()!, args), isError: false));
                }
                catch (PanelException ex)
                {
                    return Result(id, ToolResult(ex.Message, isError: true));
                }
                catch (KeyNotFoundException)
                {
                    return Error(id, -32602, $"Unknown tool: {name.GetString()}");
                }
            }
            default:
                return Error(id, -32601, $"Method not found: {m.GetString()}");
        }
    }

    private string Call(string session, string tool, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new PanelException("Arguments must be an object.");
        switch (tool)
        {
            case "show_panel":
            {
                var (panel, created) = store.Show(session, args);
                return $"{(created ? "Showing" : "Updated")} panel \"{panel.Title}\" (id {panel.Id}) in Claude Terminals, beside Git Changes" +
                       $"{(panel.Scope == "session" ? "" : panel.Scope == "project" ? ", in every session of this project" : ", in every session")}.";
            }
            case "update_panel":
            {
                var panel = store.Update(session, args);
                return $"Updated panel \"{panel.Title}\" (id {panel.Id}).";
            }
            case "get_panel":
            {
                var id = PanelSpec.ReadId(args);
                var panel = store.Get(session, id) ?? throw new PanelException($"This session has no panel \"{id}\" (it may have been closed).");
                var json = JsonSerializer.SerializeToNode(panel, GetOptions)!.AsObject();
                json.Remove("owner");
                json.Remove("version");
                return json.ToJsonString(GetOptions);
            }
            case "close_panel":
            {
                var id = PanelSpec.ReadId(args);
                return store.Close(session, id) ? $"Closed panel {id}." : $"There was no panel {id} to close.";
            }
            case "list_panels":
            {
                var mine = store.ForOwner(session);
                return mine.Count == 0
                    ? "This session has no panels."
                    : string.Join('\n', mine.Select(x =>
                        $"- {x.Id}: \"{x.Title}\" ({(x.Groups != null ? $"{x.Groups.SelectMany(PanelSpec.Flatten).Count()} groups, {x.Groups.SelectMany(PanelSpec.Flatten).Sum(g => g.Items.Count)} items" : x.Items != null ? $"{x.Items.Count} items" : "Markdown")}, shown {x.Scope switch { "project" => "in every session of this project", "everywhere" => "in every session", _ => "with this session" }}" +
                        $"{(x.Refresh != null ? $", refresh \"{x.Refresh}\"" : "")}, updated {x.Updated:u})"));
            }
            default:
                throw new KeyNotFoundException(tool);
        }
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    internal static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    // ---- the tools, as Claude sees them

    private const string ToneSchema = """{ "type": "string", "enum": ["neutral", "info", "success", "warning", "danger"] }""";

    private const string SeriesSchema = """{ "type": "array", "minItems": 2, "maxItems": 60, "items": { "type": "number" }, "description": "Numbers, oldest first, drawn as a small trend line (e.g. errors per hour)." }""";

    private const string BadgeSchema = $$"""
        { "type": "object", "properties": { "text": { "type": "string" }, "tone": {{ToneSchema}} }, "required": ["text"] }
        """;

    private const string ItemSchema = $$"""
        {
          "type": "object",
          "properties": {
            "key": { "type": "string", "description": "A short identifier shown first, e.g. an issue key like LT-475." },
            "title": { "type": "string", "description": "The item's main line." },
            "subtitle": { "type": "string", "description": "A second, dimmer line." },
            "link": { "type": "string", "description": "An http(s) address the key (or title) opens in the browser." },
            "badges": { "type": "array", "maxItems": 6, "items": {{BadgeSchema}} },
            "details": { "type": "string", "description": "Markdown the person can open under the item: a description, a stack trace, acceptance criteria." },
            "time": { "type": "string", "description": "When it happened (ISO 8601, e.g. 2026-10-23T14:05:00Z), shown as '4 min ago' and kept current." },
            "checked": { "type": "boolean", "description": "Gives the item a tick box, ticked or not. The person can tick it; get_panel reads their ticks, and sending the item again keeps them." },
            "progress": { "type": "object", "properties": { "value": { "type": "number" }, "max": { "type": "number", "default": 100 } }, "required": ["value"], "description": "A progress bar, e.g. 3 of 8 subtasks." },
            "series": {{SeriesSchema}},
            "buttons": {
              "type": "array", "maxItems": 4,
              "description": "Buttons never run anything. 'here' types the prompt into the session the person is looking at, for them to send; 'new' opens the New Session dialog with the prompt filled in, to start a new conversation; 'copy' puts text on the clipboard (a branch name, a command).",
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string", "description": "A few words, e.g. 'Start session', 'Ask here', 'Copy branch'." },
                  "target": { "type": "string", "enum": ["here", "new", "copy"], "default": "here" },
                  "prompt": { "type": "string", "description": "For 'here' and 'new': the full prompt, written so it makes sense on its own in a new conversation." },
                  "text": { "type": "string", "description": "For 'copy': the text to copy." },
                  "name": { "type": "string", "description": "For 'new': the new session's name." }
                },
                "required": ["label"]
              }
            }
          },
          "required": ["title"]
        }
        """;

    /// <summary>A group, holding items and (above the deepest level) groups of its own; spelled out per level rather than with $ref.</summary>
    private static string GroupLevel(int level)
    {
        var inner = level < PanelSpec.MaxGroupDepth
            ? $$""", "groups": { "type": "array", "description": "Groups inside this one, e.g. by priority within a status.", "items": {{GroupLevel(level + 1)}} }"""
            : "";
        return $$"""
            {
              "type": "object",
              "properties": {
                "title": { "type": "string", "description": "The group's header, e.g. 'In progress'." },
                "collapsed": { "type": "boolean", "default": false, "description": "Start folded, e.g. for a long backlog. The person's own choice is kept." },
                "items": { "type": "array", "description": "Shown before any groups inside this one.", "items": {{ItemSchema}} }{{inner}}
              },
              "required": ["title"]
            }
            """;
    }

    private static readonly string GroupSchema = GroupLevel(1);

    private const string StatsSchema = $$"""
        {
          "type": "array", "maxItems": 8,
          "description": "Figures shown as tiles above the content, e.g. open issues, errors today.",
          "items": {
            "type": "object",
            "properties": {
              "label": { "type": "string" },
              "value": { "type": ["string", "number"], "description": "Short: '214', '98%', '3 of 8'." },
              "tone": {{ToneSchema}},
              "change": { "type": "string", "enum": ["up", "down", "flat"], "description": "An arrow beside the value." },
              "note": { "type": "string", "description": "A few words under the value, e.g. '+40 since yesterday'." },
              "series": {{SeriesSchema}}
            },
            "required": ["label", "value"]
          }
        }
        """;

    private static readonly string ToolsJson = $$"""
        [
          {
            "name": "show_panel",
            "description": "Shows a panel in Claude Terminals, as a tab beside Git Changes, for the person using this session. Use it for something they want to keep in view or act on (their task list, an error summary, a checklist), not for ordinary answers. Content is markdown, a list of items, or items in collapsible groups; HTML is not rendered. Calling it again with the same id replaces that panel, keeping the person's ticks on items sent again. Panels stay until closed, even after Claude Terminals restarts.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "id": { "type": "string", "description": "This session's name for the panel (letters, digits, . _ -), e.g. 'my-tasks'." },
                "title": { "type": "string", "description": "The tab's title, a few words." },
                "subtitle": { "type": "string", "description": "Where the content came from, e.g. 'LlamaTrack · ACME'." },
                "badge": { "description": "A short count or word on the tab, e.g. 3 open errors, so it's noticed without opening the panel.", "anyOf": [ { "type": ["string", "number"] }, {{BadgeSchema}} ] },
                "stats": {{StatsSchema}},
                "markdown": { "type": "string", "description": "Markdown content (headings, lists, tables, code with a language for colouring, links). Each heading can be folded by the person, hiding everything under it up to the next heading of the same or a higher level, so organise long content under headings. Give one of markdown, items or groups." },
                "items": { "type": "array", "maxItems": 200, "description": "A list of items. Give one of markdown, items or groups.", "items": {{ItemSchema}} },
                "groups": { "type": "array", "description": "Items in collapsible groups, e.g. by status or by service; groups can hold groups, up to 3 levels (60 groups and 200 items at most in all). Give one of markdown, items or groups.", "items": {{GroupSchema}} },
                "scope": { "type": "string", "enum": ["session", "project", "everywhere"], "default": "session", "description": "Where it shows: with this session only, with every session of its project, or with every session. Used when the panel is first shown; the person can change it." },
                "refresh": { "type": "string", "description": "One line that rebuilds this panel, such as the slash command that made it. The panel's Refresh button sends it to this session." }
              },
              "required": ["id", "title"]
            }
          },
          {
            "name": "update_panel",
            "description": "Changes part of a panel this session showed earlier: only the fields given change (null clears badge or stats).",
            "inputSchema": {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "title": { "type": "string" },
                "subtitle": { "type": "string" },
                "badge": { "anyOf": [ { "type": ["string", "number", "null"] }, {{BadgeSchema}} ] },
                "stats": { "anyOf": [ { "type": "null" }, {{StatsSchema}} ] },
                "markdown": { "type": "string" },
                "items": { "type": "array", "maxItems": 200, "items": {{ItemSchema}} },
                "groups": { "type": "array", "items": {{GroupSchema}} },
                "checks": { "type": "object", "additionalProperties": { "type": "boolean" }, "description": "Ticks or unticks items yourself: { \"<key, or title for items without one>\": true }." },
                "refresh": { "type": "string" }
              },
              "required": ["id"]
            }
          },
          {
            "name": "get_panel",
            "description": "Returns one of this session's panels as it is now, including which items the person has ticked.",
            "inputSchema": { "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }
          },
          {
            "name": "close_panel",
            "description": "Closes a panel this session showed.",
            "inputSchema": { "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }
          },
          {
            "name": "list_panels",
            "description": "Lists the panels this session has showing (they outlast conversations and restarts).",
            "inputSchema": { "type": "object", "properties": {} }
          }
        ]
        """;
}
