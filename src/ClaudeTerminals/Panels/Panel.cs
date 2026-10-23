using System.Text.Json;

namespace ClaudeTerminals.Panels;

/// <summary>
/// A panel a Claude session put in the side panel, next to Git Changes: Markdown or a list of items, never HTML.
/// Identified by the session that showed it (<see cref="Owner"/>) and its own <see cref="Id"/>.
/// </summary>
internal sealed class Panel
{
    public string Owner { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    /// <summary>Set for a Markdown panel; null for a list.</summary>
    public string? Markdown { get; set; }
    /// <summary>Set for a list panel; null otherwise.</summary>
    public List<PanelItem>? Items { get; set; }
    /// <summary>Set for a list panel in collapsible groups; null otherwise. A panel has one of Markdown, Items and Groups.</summary>
    public List<PanelGroup>? Groups { get; set; }
    /// <summary>"session" (only with its own session), "project" (every session of its project) or "everywhere".</summary>
    public string Scope { get; set; } = "session";
    /// <summary>A one-line prompt that rebuilds the panel; its Refresh button sends it to the owning session.</summary>
    public string? Refresh { get; set; }
    /// <summary>A short count or word on the panel's tab, such as "3".</summary>
    public PanelBadge? Badge { get; set; }
    /// <summary>Figures shown as tiles above the content.</summary>
    public List<PanelStat>? Stats { get; set; }
    /// <summary>When Claude last changed it (the person's ticks don't count).</summary>
    public DateTime Updated { get; set; }
    /// <summary>Goes up with every change, so a tick made on an older copy of the panel isn't applied to the wrong item.</summary>
    public int Version { get; set; }
}

internal sealed class PanelItem
{
    public string? Key { get; set; }
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? Link { get; set; }
    public List<PanelBadge>? Badges { get; set; }
    public List<PanelButton>? Buttons { get; set; }
    /// <summary>Markdown shown when the person opens the item.</summary>
    public string? Details { get; set; }
    /// <summary>A moment shown as "4 min ago", kept current.</summary>
    public DateTime? Time { get; set; }
    /// <summary>Null: no tick box. Otherwise the box, ticked or not; the person can change it.</summary>
    public bool? Checked { get; set; }
    public PanelProgress? Progress { get; set; }
    /// <summary>Numbers drawn as a small trend line.</summary>
    public List<double>? Series { get; set; }

    /// <summary>What identifies the item across a refresh, for keeping its tick.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Identity => Key ?? "title:" + Title;
}

internal sealed class PanelProgress
{
    public double Value { get; set; }
    public double Max { get; set; }
}

internal sealed class PanelStat
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string Tone { get; set; } = "neutral";
    /// <summary>"up", "down" or "flat": an arrow beside the value.</summary>
    public string? Change { get; set; }
    public string? Note { get; set; }
    public List<double>? Series { get; set; }
}

internal sealed class PanelGroup
{
    public string Title { get; set; } = "";
    /// <summary>Starts folded (until the person opens it; their choice is kept).</summary>
    public bool Collapsed { get; set; }
    /// <summary>Shown first, before any groups inside this one.</summary>
    public List<PanelItem> Items { get; set; } = [];
    /// <summary>Groups inside this one (at most <see cref="PanelSpec.MaxGroupDepth"/> levels in all), or null.</summary>
    public List<PanelGroup>? Groups { get; set; }
}

internal sealed class PanelBadge
{
    public string Text { get; set; } = "";
    public string Tone { get; set; } = "neutral";
}

internal sealed class PanelButton
{
    public string Label { get; set; } = "";
    /// <summary>For "here" and "new": the prompt. For "copy": the text put on the clipboard.</summary>
    public string Prompt { get; set; } = "";
    /// <summary>
    /// "here": typed into the session the person is looking at, for them to send. "new": the New Session dialog, prompt
    /// filled in. "copy": the text goes on the clipboard.
    /// </summary>
    public string Target { get; set; } = "here";
    /// <summary>For "new": the name the new session is given.</summary>
    public string? Name { get; set; }
}

/// <summary>A mistake in what Claude sent: the message goes back to Claude so it can fix the call.</summary>
internal sealed class PanelException(string message) : Exception(message);

/// <summary>Reads a show_panel / update_panel call into a <see cref="Panel"/>, enforcing the limits.</summary>
internal static partial class PanelSpec
{
    public const int MaxPanelsPerSession = 20;
    public const int MaxMarkdown = 50_000;
    public const int MaxItems = 200;
    public const int MaxGroups = 60;
    public const int MaxGroupDepth = 3;
    public const int MaxBadges = 6;
    public const int MaxButtons = 4;
    public const int MaxPrompt = 4_000;
    public const int MaxDetails = 20_000;
    public const int MaxStats = 8;
    public const int MaxSeries = 60;

    public static readonly string[] Scopes = ["session", "project", "everywhere"];
    public static readonly string[] Tones = ["neutral", "info", "success", "warning", "danger"];
    public static readonly string[] Targets = ["here", "new", "copy"];
    public static readonly string[] Changes = ["up", "down", "flat"];

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial System.Text.RegularExpressions.Regex IdPattern();

    public static string ReadId(JsonElement args)
    {
        var id = String(args, "id", 64, required: true)!;
        return IdPattern().IsMatch(id) ? id : throw new PanelException("id may only use letters, digits, '.', '_' and '-' (at most 64).");
    }

    /// <summary>
    /// Applies the fields present in <paramref name="args"/> to <paramref name="panel"/>. For a new panel (<paramref name="creating"/>)
    /// a title and content (markdown or items) are required; an update changes only what it names.
    /// </summary>
    public static void Apply(Panel panel, JsonElement args, bool creating)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new PanelException("Arguments must be an object.");
        if (creating || args.TryGetProperty("title", out _)) panel.Title = String(args, "title", 60, required: true)!;
        if (args.TryGetProperty("subtitle", out _)) panel.Subtitle = String(args, "subtitle", 120);

        var hasMarkdown = Present(args, "markdown");
        var hasItems = Present(args, "items");
        var hasGroups = Present(args, "groups");
        if ((hasMarkdown ? 1 : 0) + (hasItems ? 1 : 0) + (hasGroups ? 1 : 0) > 1) throw new PanelException("Give one of markdown, items or groups.");
        if (creating && !hasMarkdown && !hasItems && !hasGroups) throw new PanelException("Give the panel's content: markdown, items or groups.");
        if (hasMarkdown)
        {
            var markdown = args.GetProperty("markdown");
            if (markdown.ValueKind != JsonValueKind.String) throw new PanelException("markdown must be a string.");
            var text = markdown.GetString()!;
            if (text.Length > MaxMarkdown) throw new PanelException($"markdown is too long ({text.Length} characters; at most {MaxMarkdown}).");
            (panel.Markdown, panel.Items, panel.Groups) = (text, null, null);
        }
        if (hasItems) (panel.Markdown, panel.Items, panel.Groups) = (null, Items(args.GetProperty("items")), null);
        if (hasGroups) (panel.Markdown, panel.Items, panel.Groups) = (null, null, Groups(args.GetProperty("groups")));

        // null clears these on an update; missing leaves them as they were.
        if (args.TryGetProperty("badge", out var badge))
            panel.Badge = badge.ValueKind == JsonValueKind.Null ? null : Badge(badge, 12);
        if (args.TryGetProperty("stats", out var stats))
            panel.Stats = stats.ValueKind == JsonValueKind.Null ? null : Stats(stats);

        if (creating && Present(args, "scope")) panel.Scope = OneOf(args, "scope", Scopes)!;
        if (args.TryGetProperty("refresh", out _))
        {
            var refresh = String(args, "refresh", 500);
            if (refresh != null && refresh.IndexOfAny(['\r', '\n']) >= 0) throw new PanelException("refresh must be one line (it is sent to the session as typed).");
            panel.Refresh = refresh;
        }
    }

    private static List<PanelGroup> Groups(JsonElement groups)
    {
        var list = Groups(groups, 1);
        var all = list.SelectMany(Flatten).ToList();
        if (all.Count > MaxGroups) throw new PanelException($"Too many groups ({all.Count} in all; at most {MaxGroups}).");
        var total = all.Sum(g => g.Items.Count);
        if (total > MaxItems) throw new PanelException($"Too many items across the groups ({total}; at most {MaxItems}).");
        return list;
    }

    /// <summary>A group and every group inside it.</summary>
    internal static IEnumerable<PanelGroup> Flatten(PanelGroup g) => g.Groups == null ? [g] : g.Groups.SelectMany(Flatten).Prepend(g);

    private static List<PanelGroup> Groups(JsonElement groups, int depth)
    {
        if (groups.ValueKind != JsonValueKind.Array) throw new PanelException("groups must be an array.");
        var list = new List<PanelGroup>();
        foreach (var g in groups.EnumerateArray())
        {
            var at = $"groups[{list.Count}]";
            if (g.ValueKind != JsonValueKind.Object) throw new PanelException($"{at} must be an object with a title, and items or groups.");
            try
            {
                var nested = Present(g, "groups");
                if (nested && depth >= MaxGroupDepth) throw new PanelException($"Groups go at most {MaxGroupDepth} levels deep.");
                list.Add(new PanelGroup
                {
                    Title = String(g, "title", 80, required: true)!,
                    Collapsed = g.TryGetProperty("collapsed", out var c) && c.ValueKind == JsonValueKind.True,
                    Items = Present(g, "items") ? Items(g.GetProperty("items")) : [],
                    Groups = nested ? Groups(g.GetProperty("groups"), depth + 1) : null,
                });
            }
            catch (PanelException ex)
            {
                throw new PanelException($"{at}: {ex.Message}");
            }
        }
        return list;
    }

    private static List<PanelItem> Items(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) throw new PanelException("items must be an array.");
        if (items.GetArrayLength() > MaxItems) throw new PanelException($"Too many items ({items.GetArrayLength()}; at most {MaxItems}).");
        var list = new List<PanelItem>();
        var n = 0;
        foreach (var e in items.EnumerateArray())
        {
            n++;
            if (e.ValueKind != JsonValueKind.Object) throw new PanelException($"items[{n - 1}] must be an object.");
            try
            {
                list.Add(new PanelItem
                {
                    Key = String(e, "key", 40),
                    Title = String(e, "title", 200, required: true)!,
                    Subtitle = String(e, "subtitle", 300),
                    Link = Link(e),
                    Badges = Badges(e),
                    Buttons = Buttons(e),
                    Details = Details(e),
                    Time = Time(e),
                    Checked = Present(e, "checked") ? Bool(e, "checked") : null,
                    Progress = Progress(e),
                    Series = Series(e),
                });
            }
            catch (PanelException ex)
            {
                throw new PanelException($"items[{n - 1}]: {ex.Message}");
            }
        }
        return list;
    }

    private static string? Link(JsonElement item)
    {
        var link = String(item, "link", 2000);
        if (link == null) return null;
        return Uri.TryCreate(link, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u.AbsoluteUri
            : throw new PanelException("link must be an http or https address.");
    }

    private static List<PanelBadge>? Badges(JsonElement item)
    {
        if (!Present(item, "badges")) return null;
        var badges = item.GetProperty("badges");
        if (badges.ValueKind != JsonValueKind.Array) throw new PanelException("badges must be an array.");
        if (badges.GetArrayLength() > MaxBadges) throw new PanelException($"At most {MaxBadges} badges.");
        return badges.EnumerateArray().Select(b => Badge(b, 30)).ToList();
    }

    /// <summary>A badge: { text, tone } or just the text.</summary>
    private static PanelBadge Badge(JsonElement b, int max) => b.ValueKind switch
    {
        JsonValueKind.String when b.GetString()!.Trim().Length > 0 => new PanelBadge { Text = Clip(b.GetString()!.Trim(), max) },
        JsonValueKind.Number => new PanelBadge { Text = Clip(b.GetRawText(), max) },
        JsonValueKind.Object => new PanelBadge { Text = String(b, "text", max, required: true)!, Tone = OneOf(b, "tone", Tones) ?? "neutral" },
        _ => throw new PanelException("A badge is an object with text (and optionally tone)."),
    };

    private static List<PanelStat> Stats(JsonElement stats)
    {
        if (stats.ValueKind != JsonValueKind.Array) throw new PanelException("stats must be an array.");
        if (stats.GetArrayLength() > MaxStats) throw new PanelException($"At most {MaxStats} stats.");
        var list = new List<PanelStat>();
        foreach (var s in stats.EnumerateArray())
        {
            var at = $"stats[{list.Count}]";
            if (s.ValueKind != JsonValueKind.Object) throw new PanelException($"{at} must be an object with label and value.");
            try
            {
                var value = s.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : String(s, "value", 20, required: true)!;
                list.Add(new PanelStat
                {
                    Label = String(s, "label", 30, required: true)!,
                    Value = Clip(value, 20),
                    Tone = OneOf(s, "tone", Tones) ?? "neutral",
                    Change = OneOf(s, "change", Changes),
                    Note = String(s, "note", 60),
                    Series = Series(s),
                });
            }
            catch (PanelException ex)
            {
                throw new PanelException($"{at}: {ex.Message}");
            }
        }
        return list;
    }

    private static string? Details(JsonElement item)
    {
        if (!Present(item, "details")) return null;
        var v = item.GetProperty("details");
        if (v.ValueKind != JsonValueKind.String) throw new PanelException("details must be a string (Markdown).");
        var text = v.GetString()!;
        if (text.Length > MaxDetails) throw new PanelException($"details is too long (at most {MaxDetails} characters).");
        return text.Trim().Length == 0 ? null : text;
    }

    private static DateTime? Time(JsonElement item)
    {
        var text = String(item, "time", 60);
        if (text == null) return null;
        return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.UtcDateTime
            : throw new PanelException("time must be a date and time, such as 2026-10-23T14:05:00Z.");
    }

    private static PanelProgress? Progress(JsonElement item)
    {
        if (!Present(item, "progress")) return null;
        var p = item.GetProperty("progress");
        if (p.ValueKind != JsonValueKind.Object) throw new PanelException("progress must be an object with value and max.");
        var value = Number(p, "value");
        var max = p.TryGetProperty("max", out _) ? Number(p, "max") : 100;
        if (max <= 0) throw new PanelException("progress.max must be more than 0.");
        if (value < 0 || value > max) throw new PanelException("progress.value must be between 0 and max.");
        return new PanelProgress { Value = value, Max = max };
    }

    private static List<double>? Series(JsonElement e)
    {
        if (!Present(e, "series")) return null;
        var s = e.GetProperty("series");
        if (s.ValueKind != JsonValueKind.Array) throw new PanelException("series must be an array of numbers.");
        var n = s.GetArrayLength();
        if (n < 2 || n > MaxSeries) throw new PanelException($"series needs 2 to {MaxSeries} numbers.");
        return s.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && double.IsFinite(x.GetDouble())
            ? x.GetDouble()
            : throw new PanelException("series must be an array of numbers.")).ToList();
    }

    private static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble())
            ? v.GetDouble()
            : throw new PanelException($"{name} must be a number.");

    private static bool Bool(JsonElement e, string name) => e.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new PanelException($"{name} must be true or false."),
    };

    /// <summary>Every item in a panel, wherever it sits.</summary>
    public static IEnumerable<PanelItem> AllItems(Panel p) =>
        (p.Items ?? []).Concat((p.Groups ?? []).SelectMany(Flatten).SelectMany(g => g.Items));

    /// <summary>
    /// After Claude sends a panel's items again, items it sends with a tick box keep the person's tick (matched by key,
    /// or by title when there's no key): refreshing a checklist mustn't undo their work.
    /// </summary>
    public static void KeepTicks(Panel from, Panel to)
    {
        var ticks = new Dictionary<string, bool>();
        foreach (var item in AllItems(from)) if (item.Checked is { } c) ticks.TryAdd(item.Identity, c);
        foreach (var item in AllItems(to))
            if (item.Checked != null && ticks.TryGetValue(item.Identity, out var c)) item.Checked = c;
    }

    /// <summary>update_panel's "checks": { "<key or title>": true|false } ticks or unticks those items itself.</summary>
    public static void ApplyChecks(Panel panel, JsonElement args)
    {
        if (!Present(args, "checks")) return;
        var checks = args.GetProperty("checks");
        if (checks.ValueKind != JsonValueKind.Object) throw new PanelException("checks must be an object of item key (or title) to true/false.");
        var items = AllItems(panel).ToList();
        foreach (var c in checks.EnumerateObject())
        {
            if (c.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new PanelException($"checks.{c.Name} must be true or false.");
            var matches = items.Where(i => i.Key == c.Name || (i.Key == null && i.Title == c.Name)).ToList();
            if (matches.Count == 0) throw new PanelException($"checks: no item has the key or title \"{c.Name}\".");
            foreach (var i in matches) i.Checked = c.Value.ValueKind == JsonValueKind.True;
        }
    }

    private static List<PanelButton>? Buttons(JsonElement item)
    {
        if (!Present(item, "buttons")) return null;
        var buttons = item.GetProperty("buttons");
        if (buttons.ValueKind != JsonValueKind.Array) throw new PanelException("buttons must be an array.");
        if (buttons.GetArrayLength() > MaxButtons) throw new PanelException($"At most {MaxButtons} buttons.");
        return buttons.EnumerateArray().Select(b =>
        {
            if (b.ValueKind != JsonValueKind.Object) throw new PanelException("Each button is an object with label and prompt (or text, to copy).");
            var target = OneOf(b, "target", Targets) ?? (Present(b, "text") && !Present(b, "prompt") ? "copy" : "here");
            // A copy button carries text; the others a prompt (either name is accepted for either).
            var field = target == "copy" ? (Present(b, "text") ? "text" : "prompt") : (Present(b, "prompt") ? "prompt" : "text");
            var prompt = String(b, field, int.MaxValue, required: false)
                ?? throw new PanelException(target == "copy" ? "A copy button needs text." : "prompt is required.");
            if (prompt.Length > MaxPrompt) throw new PanelException($"A button's {field} is too long (at most {MaxPrompt} characters).");
            return new PanelButton
            {
                Label = String(b, "label", 40, required: true)!,
                Prompt = prompt,
                Target = target,
                Name = String(b, "name", 60),
            };
        }).ToList();
    }

    /// <summary>True when the property is there and not null.</summary>
    private static bool Present(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    /// <summary>A string field, trimmed; too long is cut short (with …) rather than refused. Empty counts as missing.</summary>
    private static string? String(JsonElement e, string name, int max, bool required = false)
    {
        if (!Present(e, name))
            return required ? throw new PanelException($"{name} is required.") : null;
        var v = e.GetProperty(name);
        if (v.ValueKind != JsonValueKind.String) throw new PanelException($"{name} must be a string.");
        var text = v.GetString()!.Trim();
        if (text.Length == 0) return required ? throw new PanelException($"{name} is required.") : null;
        return Clip(text, max);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string? OneOf(JsonElement e, string name, string[] allowed)
    {
        var v = String(e, name, 40);
        if (v == null) return null;
        return allowed.FirstOrDefault(a => a.Equals(v, StringComparison.OrdinalIgnoreCase))
            ?? throw new PanelException($"{name} must be one of: {string.Join(", ", allowed)}.");
    }
}
