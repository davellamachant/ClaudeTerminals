using System.Text.Json;

namespace LlamaTree.Themes;

/// <summary>
/// A theme's own status dots ("dots" in its file): a picture for each state a session can be in, and how it moves. While
/// the theme is on they're one more style in Status dots… (as "&lt;theme&gt;'s own"), picked until you pick another. Only
/// data, as the light cycle's picture is (<see cref="ThemeCycle"/>): shapes and numbers the app checks and draws itself,
/// and a motion from a fixed list, so a theme can't bring markup, a script or a style of its own with them.
/// </summary>
/// <param name="States">The pictures by state (<see cref="StateNames"/>); a state left out is a plain dot in its colour.</param>
/// <param name="Size">How big each picture is drawn, in pixels, <see cref="MinSize"/> to <see cref="MaxSize"/>, centred on the 8px dot.</param>
/// <param name="About">A sentence for its card in Status dots…, or null.</param>
internal sealed record ThemeDots(IReadOnlyDictionary<string, ThemeDot> States, int Size, string? About)
{
    /// <summary>
    /// The states, as the floor's session counts name them: idle (running, waiting for nothing), working, waiting (done
    /// while you were away), asking (needs your answer) and stopped (not running).
    /// </summary>
    public static readonly IReadOnlyList<string> StateNames = ["idle", "working", "waiting", "asking", "stopped"];

    /// <summary>How a picture can move: each an animation of the app's own (see .dot in app.css). None while Windows' animation effects are off.</summary>
    public static readonly IReadOnlyList<string> Motions = ["none", "spin", "pulse", "breathe", "blink", "beat", "hop", "shake"];

    public const int MinSize = 6, MaxSize = 14, DefaultSize = 10, MaxAbout = 200;

    /// <summary>
    /// The colours a dot's shape can take: "status" (the colour of the state it shows), "accent", "none", one of the
    /// page's colours by name, or #rgb, #rrggbb or #rrggbbaa.
    /// </summary>
    internal static bool IsColour(string value) =>
        value is "status" or "accent" or "none" || ThemeCatalog.ColorNames.Contains(value) || ThemeCycle.Hex().IsMatch(value);

    /// <summary>Reads a theme's "dots"; what isn't valid is left out, with why, and null if no state has a picture.</summary>
    internal static ThemeDots? Parse(JsonElement e, string fileName, List<string> problems)
    {
        if (!e.TryGetProperty("dots", out var d)) return null;
        if (d.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{fileName}: dots is an object of a picture for each state ({string.Join(", ", StateNames)}).");
            return null;
        }
        void Problem(string what) => problems.Add($"{fileName}: dots.{what}");
        var states = new Dictionary<string, ThemeDot>();
        foreach (var p in d.EnumerateObject())
        {
            if (p.Name is "size" or "about") continue;
            if (!StateNames.Contains(p.Name))
            {
                Problem($"{p.Name} isn't a state; the states are {string.Join(", ", StateNames)}.");
                continue;
            }
            if (p.Value.ValueKind != JsonValueKind.Object || !p.Value.TryGetProperty("image", out _))
                Problem($"{p.Name} needs an \"image\" (and a \"motion\" if it moves); it's a plain dot.");
            else if (ParseState(p.Value, (w) => Problem($"{p.Name}.{w}")) is { } dot) states[p.Name] = dot;
        }
        if (states.Count == 0)
        {
            problems.Add($"{fileName}: dots has no state with a picture the app can draw; the theme keeps the usual dots.");
            return null;
        }
        string? about = null;
        if (d.TryGetProperty("about", out var a))
        {
            if (a.ValueKind == JsonValueKind.String && a.GetString()!.Trim() is { Length: > 0 and <= MaxAbout } text) about = text;
            else Problem($"about is a sentence of up to {MaxAbout} characters.");
        }
        return new ThemeDots(states, ThemeCycle.Whole(d, "size", MinSize, MaxSize, DefaultSize, Problem), about);
    }

    private static ThemeDot? ParseState(JsonElement s, Action<string> problem)
    {
        var img = s.GetProperty("image");
        foreach (var p in s.EnumerateObject())
            if (p.Name is not ("image" or "motion")) problem($"{p.Name} isn't something a dot has.");
        var image = ThemeCycle.ParseImage(img, problem, IsColour, "status",
            "\"status\", \"accent\", one of the page's colours, #rrggbb or \"none\"", "it's a plain dot");
        if (image == null) return null;
        var motion = "none";
        if (s.TryGetProperty("motion", out var m))
        {
            if (m.ValueKind == JsonValueKind.String && Motions.Contains(m.GetString())) motion = m.GetString()!;
            else problem($"motion is one of {string.Join(", ", Motions)}; it stands still.");
        }
        return new ThemeDot(image, motion);
    }
}

/// <param name="Image">The picture: shapes in its own viewBox, fitted to the dot's size.</param>
/// <param name="Motion">One of <see cref="ThemeDots.Motions"/>.</param>
internal sealed record ThemeDot(CycleImage Image, string Motion);
