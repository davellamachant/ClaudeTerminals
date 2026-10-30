using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeTerminals.Themes;

/// <summary>
/// A theme: colours for the whole app, as data. <see cref="Id"/> is its file's name (nerds.json is "nerds").
/// </summary>
/// <param name="Base">"dark" or "light": what fills in every colour it leaves out, and what Claude and plugins are told.</param>
/// <param name="Font">A font family for the app's own text (not the terminals'), or null for the usual one.</param>
/// <param name="Glow">Text glows faintly in the accent colour, as on a phosphor screen.</param>
/// <param name="Monochrome">
/// Everything in shades of the accent colour picked in Accent colour: the page builds the colours from it, under any the
/// theme sets itself, and builds them again when the accent changes.
/// </param>
/// <param name="Accent">Its own accent, in place of the one picked in Accent colour; null keeps that.</param>
/// <param name="Colors">The page's colours by name (see <see cref="ThemeCatalog.ColorNames"/>).</param>
/// <param name="Terminal">The terminals' colours, by xterm.js's names (see <see cref="ThemeCatalog.TerminalNames"/>).</param>
/// <param name="Diff">The diff editor's colours (see <see cref="ThemeCatalog.DiffNames"/>).</param>
/// <param name="BuiltIn">Shipped with the app, rather than added to the themes folder.</param>
internal sealed record ThemeInfo(string Id, string Name, string Base, string? Font, bool Glow, bool Monochrome, ThemeAccent? Accent,
    IReadOnlyDictionary<string, string> Colors, IReadOnlyDictionary<string, string> Terminal, IReadOnlyDictionary<string, string> Diff,
    bool BuiltIn);

/// <param name="Line">Lines, ticks and highlights: readable on the theme's background.</param>
/// <param name="Strong">Fills that carry white text: active tabs, buttons, menus.</param>
internal sealed record ThemeAccent(string Line, string Strong);

/// <summary>
/// Finds the themes: those shipped in wwwroot/themes, and those in the profile's themes folder. Each is a JSON file such as
/// <c>{ "name": "Nerds", "base": "dark", "font": "Cascadia Mono", "glow": true, "monochrome": true, "accent": { "line": "#39ff6a", "strong": "#0b6b26" },
/// "colors": { "chrome": "#050a06", … }, "terminal": { "background": "#020703", … }, "diff": { "insertedLine": "#1a5a2a55", … } }</c>.
/// A theme is only data: names the app knows and colour values it checks, so a theme can't restyle the page any other
/// way, or load anything.
/// </summary>
internal static partial class ThemeCatalog
{
    /// <summary>The names a theme can't take: the app's own choices in the theme menu.</summary>
    private static readonly HashSet<string> Reserved = ["system", "light", "dark"];

    private const int MaxFileBytes = 64 * 1024;

    /// <summary>The page's colours a theme can set: its stylesheet's variables (--chrome, --text-dim, …) without the dashes.</summary>
    public static readonly IReadOnlySet<string> ColorNames = new HashSet<string>(
    [
        "chrome", "chrome-2", "chrome-3", "panel", "panel-2", "hover", "hover-strong", "border", "border-soft", "border-strong",
        "text", "text-dim", "text-faint", "text-strong", "focus", "select", "select-inactive", "terminal-bg", "editor-bg",
        "ok", "working", "attention", "asking", "danger", "danger-fill", "st-m", "st-a", "st-d", "st-r", "st-u",
        "info-bg", "info-border", "error-bg", "error-border", "pv-warn-bg", "pv-warn-border", "ico-folder", "ico-file", "graph",
        "ref-bg", "ref-fg", "ref-head-bg", "ref-head-fg", "ref-tag-bg", "ref-tag-fg", "scroll-thumb", "scroll-thumb-hover",
        "tab-border-shown", "tab-border-hover", "tick", "tick-active", "tick-hot", "card-bg", "card-border", "bubble",
        "code-fg", "code-bg", "well", "table-head", "link", "update-fg",
    ]);

    /// <summary>The terminals' colours a theme can set (xterm.js's theme).</summary>
    public static readonly IReadOnlySet<string> TerminalNames = new HashSet<string>(
    [
        "background", "foreground", "cursor", "cursorAccent", "selectionBackground", "selectionForeground", "selectionInactiveBackground",
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
        "overviewRulerBorder", "scrollbarSliderBackground", "scrollbarSliderHoverBackground", "scrollbarSliderActiveBackground",
    ]);

    /// <summary>
    /// The diff editor's colours a theme can set: the editor's own, the changes', and the syntax colours (comment,
    /// keyword, string, number, type). The editor only takes #rrggbb or #rrggbbaa.
    /// </summary>
    public static readonly IReadOnlySet<string> DiffNames = new HashSet<string>(
    [
        "background", "foreground", "lineNumber", "lineNumberActive", "selection", "cursor", "lineHighlightBorder",
        "insertedLine", "insertedText", "removedLine", "removedText", "insertedGutter", "removedGutter",
        "insertedOverview", "removedOverview", "diagonalFill", "scrollbar",
        "comment", "keyword", "string", "number", "type",
    ]);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(?:#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})|rgba?\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*(?:,\s*(?:0|1|0?\.\d+)\s*)?\))$")]
    private static partial Regex CssColor();

    [GeneratedRegex("^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColor();

    // A plain family name: nothing that could end the font-family value and start another rule.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 \-]{0,59}$")]
    private static partial Regex FontName();

    /// <summary>
    /// The themes in <paramref name="builtIn"/> (shipped with the app) and then <paramref name="user"/> (the profile's
    /// themes folder), each by name. A file that isn't a theme is left out, and a value that isn't valid is left out of
    /// its theme, with why.
    /// </summary>
    public static (List<ThemeInfo> Themes, List<string> Problems) Load(string builtIn, string user)
    {
        var themes = new List<ThemeInfo>();
        var problems = new List<string>();
        foreach (var (folder, isBuiltIn) in new[] { (builtIn, true), (user, false) })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(file);
                var id = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (!IdPattern().IsMatch(id) || Reserved.Contains(id))
                {
                    problems.Add($"{fileName}: name the file with letters, digits and hyphens, and not light, dark or system.");
                    continue;
                }
                if (themes.Any(t => t.Id == id))
                {
                    problems.Add($"{fileName}: there's already a theme called {id}.");
                    continue;
                }
                try
                {
                    if (new FileInfo(file).Length > MaxFileBytes)
                    {
                        problems.Add($"{fileName}: it's larger than {MaxFileBytes / 1024} KB.");
                        continue;
                    }
                    using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (Parse(id, doc.RootElement, isBuiltIn, fileName, problems) is { } theme) themes.Add(theme);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{fileName}: {ex.Message}");
                }
            }
        }
        themes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return (themes, problems);
    }

    /// <summary>
    /// Writes <paramref name="theme"/> to <paramref name="folder"/> as a new file named after its name (my-dark.json; a
    /// number is added when that's taken), once it checks out as a theme with nothing left out. Returns the new theme's id.
    /// </summary>
    public static string Save(string folder, string builtIn, JsonElement theme)
    {
        var name = theme.ValueKind == JsonValueKind.Object ? Str(theme, "name")?.Trim() : null;
        if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Give the theme a name.");
        var stem = Slug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (stem.Length > 32) stem = stem[..32].Trim('-');
        if (stem.Length == 0 || Reserved.Contains(stem)) stem = "my-theme";
        var problems = new List<string>();
        if (Parse(stem, theme, false, stem + ".json", problems) == null || problems.Count > 0)
            throw new InvalidOperationException("The theme couldn't be saved: " + string.Join(" ", problems));

        Directory.CreateDirectory(folder);
        var taken = Load(builtIn, folder).Themes.Select(t => t.Id).ToHashSet();
        var id = stem;
        for (var n = 2; taken.Contains(id) || File.Exists(Path.Combine(folder, id + ".json")); n++) id = $"{stem}-{n}";
        File.WriteAllText(Path.Combine(folder, id + ".json"),
            JsonSerializer.Serialize(theme, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return id;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Slug();

    internal static ThemeInfo? Parse(string id, JsonElement e, bool builtIn, string fileName, List<string> problems)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{fileName}: a theme is a JSON object.");
            return null;
        }
        var name = Str(e, "name")?.Trim() is { Length: > 0 and <= 40 } n ? n : id;
        var @base = Str(e, "base") ?? "dark";
        if (@base is not ("dark" or "light"))
        {
            problems.Add($"{fileName}: base is \"dark\" or \"light\".");
            return null;
        }
        string? font = null;
        if (Str(e, "font") is { } f)
        {
            if (FontName().IsMatch(f)) font = f;
            else problems.Add($"{fileName}: font \"{f}\" isn't a font family name (letters, digits, spaces and hyphens).");
        }
        var glow = e.TryGetProperty("glow", out var g) && g.ValueKind == JsonValueKind.True;
        var monochrome = e.TryGetProperty("monochrome", out var m) && m.ValueKind == JsonValueKind.True;

        ThemeAccent? accent = null;
        if (e.TryGetProperty("accent", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            if (Str(a, "line") is { } line && Str(a, "strong") is { } strong && CssColor().IsMatch(line) && CssColor().IsMatch(strong))
                accent = new ThemeAccent(line, strong);
            else problems.Add($"{fileName}: accent needs a \"line\" and a \"strong\" colour.");
        }

        return new ThemeInfo(id, name, @base, font, glow, monochrome, accent,
            Colors(e, "colors", ColorNames, CssColor(), fileName, problems),
            Colors(e, "terminal", TerminalNames, CssColor(), fileName, problems),
            Colors(e, "diff", DiffNames, HexColor(), fileName, problems),
            builtIn);
    }

    /// <summary>One section's colours: names it knows, with valid values; the rest are left out, with why.</summary>
    private static Dictionary<string, string> Colors(JsonElement e, string section, IReadOnlySet<string> names, Regex valid, string fileName,
        List<string> problems)
    {
        var result = new Dictionary<string, string>();
        if (!e.TryGetProperty(section, out var s) || s.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in s.EnumerateObject())
        {
            if (!names.Contains(p.Name))
                problems.Add($"{fileName}: {section} has no colour called \"{p.Name}\".");
            else if (p.Value.ValueKind != JsonValueKind.String || !valid.IsMatch(p.Value.GetString()!))
                problems.Add($"{fileName}: {section}.{p.Name} isn't a colour{(section == "diff" ? " (#rrggbb or #rrggbbaa)" : "")}.");
            else
                result[p.Name] = p.Value.GetString()!;
        }
        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
