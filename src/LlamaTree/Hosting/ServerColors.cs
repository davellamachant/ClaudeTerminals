using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LlamaTree.Hosting;

/// <summary>
/// The colours in a server's messages to a --remote window's page, which puts them into style text (an agent's tag, a
/// project's outline): anything but a plain hex colour there would be more CSS, so it's taken out (the page then uses
/// its own colour). The page checks them too (cssColor in app.js); this is the window's side of that.
/// </summary>
internal static partial class ServerColors
{
    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\z")]
    private static partial Regex HexColor();

    /// <summary>Whether <paramref name="color"/> is a #rgb, #rgba, #rrggbb or #rrggbbaa, and nothing else.</summary>
    public static bool IsColor(string? color) => color != null && HexColor().IsMatch(color);

    /// <summary>
    /// <paramref name="json"/> with the session list's agent and project colours that aren't colours taken out (null),
    /// or as it was when there are none (any other message, or one that isn't JSON, goes as it came).
    /// </summary>
    public static string Sanitize(string json)
    {
        // Only the session list carries them; the rest (terminal output above all) isn't parsed.
        if (!json.Contains("\"sessions\"", StringComparison.Ordinal)) return json;
        // One that can't be read as it's sent (a repeated name, say) goes as it came: the page checks the colours too.
        try { return Sanitized(json); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { return json; }
    }

    private static string Sanitized(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject msg || msg["type"] is not JsonValue t || !t.TryGetValue<string>(out var type) || type != "sessions") return json;
        var changed = false;
        if (msg["agents"] is JsonArray agents)
            foreach (var a in agents.OfType<JsonObject>())
                if (a["color"] is { } c && !(c is JsonObject o && IsColor(Str(o["dark"])) && IsColor(Str(o["light"]))))
                {
                    a["color"] = null;
                    changed = true;
                }
        if (msg["projects"] is JsonArray projects)
            foreach (var p in projects.OfType<JsonObject>())
                if (p["color"] is { } c && !IsColor(Str(c)))
                {
                    p["color"] = null;
                    changed = true;
                }
        return changed ? msg.ToJsonString() : json;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
