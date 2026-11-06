using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaTree.Themes;

/// <summary>
/// A theme's own light cycle ("cycle" in its file, with "cursor": "light-cycle"): its picture and how it glows and leaves its
/// ribbon. Only data: the picture is shapes and numbers the app checks and draws itself (never a theme's own SVG, which
/// could carry scripts, links or styles), and the rest are numbers within limits. Anything left out keeps the app's own
/// cycle as it is.
/// </summary>
/// <param name="Image">The picture, or null for the app's own.</param>
/// <param name="Size">How many cells wide it's drawn, 1 to <see cref="MaxSize"/>, one row high.</param>
/// <param name="Offset">How far right of the cursor it rides, in pixels, 0 to 60: clear of what you're typing.</param>
/// <param name="Glow">How far its glow reaches, in pixels, 0 to 20.</param>
/// <param name="Trail">The ribbon it leaves, or null for none.</param>
internal sealed partial record ThemeCycle(CycleImage? Image, int Size, int Offset, double Glow, CycleTrail? Trail)
{
    public const int MaxSize = 6, MaxShapes = 64, MaxPath = 4000, MaxPoints = 200;
    public static readonly CycleTrail DefaultTrail = new(2, 10, 800, 0.69, "cursor");
    public static readonly ThemeCycle Default = new(null, 3, 25, 6, DefaultTrail);

    /// <summary>
    /// The colours a shape or the ribbon can take: "cursor" (the terminal's cursor colour, as the app's own cycle has),
    /// "accent", "none", one of the page's colours by name, or #rgb, #rrggbb or #rrggbbaa.
    /// </summary>
    internal static bool IsColour(string value) =>
        value is "cursor" or "accent" or "none" || ThemeCatalog.ColorNames.Contains(value) || Hex().IsMatch(value);

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    internal static partial Regex Hex();

    // Path data: its commands and numbers, nothing else, starting with a move.
    [GeneratedRegex(@"^\s*[Mm][MmLlHhVvCcSsQqTtAaZz0-9eE.,+\-\s]*$")]
    internal static partial Regex PathData();

    /// <summary>Reads a theme's "cycle"; a value that isn't valid is left at the app's own, with why.</summary>
    internal static ThemeCycle? Parse(JsonElement e, string fileName, List<string> problems)
    {
        if (!e.TryGetProperty("cycle", out var c)) return null;
        if (c.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{fileName}: cycle is an object of the light cycle's picture and options.");
            return null;
        }
        void Problem(string what) => problems.Add($"{fileName}: cycle.{what}");
        foreach (var p in c.EnumerateObject())
            if (p.Name is not ("image" or "size" or "offset" or "glow" or "trail")) Problem($"{p.Name} isn't something the light cycle has.");

        var image = c.TryGetProperty("image", out var img) ? ParseImage(img, Problem) : null;
        var size = Whole(c, "size", 1, MaxSize, Default.Size, Problem);
        var offset = Whole(c, "offset", 0, 60, Default.Offset, Problem);
        var glow = Number(c, "glow", 0, 20, Default.Glow, Problem);
        var trail = DefaultTrail;
        if (c.TryGetProperty("trail", out var t))
        {
            if (t.ValueKind == JsonValueKind.False) trail = null;
            else if (t.ValueKind != JsonValueKind.Object) Problem("trail is false (no ribbon) or its options.");
            else
            {
                foreach (var p in t.EnumerateObject())
                    if (p.Name is not ("width" or "glow" or "fade" or "at" or "colour")) Problem($"trail.{p.Name} isn't something the ribbon has.");
                var colour = DefaultTrail.Colour;
                if (t.TryGetProperty("colour", out var tc))
                {
                    if (tc.ValueKind == JsonValueKind.String && IsColour(tc.GetString()!) && tc.GetString() != "none") colour = tc.GetString()!;
                    else Problem("trail.colour is \"cursor\", \"accent\", one of the page's colours, or #rrggbb.");
                }
                trail = new CycleTrail(
                    Number(t, "width", 1, 6, DefaultTrail.Width, (w) => Problem("trail." + w)),
                    Number(t, "glow", 0, 20, DefaultTrail.Glow, (w) => Problem("trail." + w)),
                    Whole(t, "fade", 200, 3000, DefaultTrail.Fade, (w) => Problem("trail." + w)),
                    Number(t, "at", 0, 1, DefaultTrail.At, (w) => Problem("trail." + w)),
                    colour);
            }
        }
        return new ThemeCycle(image, size, offset, glow, trail);
    }

    /// <summary>
    /// A picture: its "viewBox" and "shapes" (see ParseShape), as the light cycle and a theme's status dots (<see cref="ThemeDots"/>)
    /// have. <paramref name="colours"/> says what a shape's colour can be (<see cref="IsColour"/> if left out), described by
    /// <paramref name="colourHelp"/>; <paramref name="stroke"/> is a shape's stroke when it gives none, and <paramref name="kept"/>
    /// says what stands in for a picture that can't be used.
    /// </summary>
    internal static CycleImage? ParseImage(JsonElement img, Action<string> problem, Func<string, bool>? colours = null, string stroke = "cursor",
        string colourHelp = "\"cursor\", \"accent\", one of the page's colours, #rrggbb or \"none\"", string kept = "the app's own picture is kept")
    {
        colours ??= IsColour;
        if (img.ValueKind != JsonValueKind.Object || !img.TryGetProperty("viewBox", out var vb) || !img.TryGetProperty("shapes", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            problem($"image needs a \"viewBox\" ([x, y, width, height]) and its \"shapes\"; {kept}.");
            return null;
        }
        if (Numbers(vb) is not { Length: 4 } box || box[2] <= 0 || box[3] <= 0 || box[2] > 1000 || box[3] > 1000)
        {
            problem($"image.viewBox is [x, y, width, height], each width and height above 0 and up to 1000; {kept}.");
            return null;
        }
        var shapes = new List<CycleShape>();
        var i = 0;
        foreach (var s in list.EnumerateArray())
        {
            i++;
            if (shapes.Count == MaxShapes)
            {
                problem($"image has more than {MaxShapes} shapes; the rest are left out.");
                break;
            }
            if (ParseShape(s, $"image.shapes[{i - 1}]", problem, colours, stroke, colourHelp) is { } shape) shapes.Add(shape);
        }
        if (shapes.Count == 0)
        {
            problem($"image has no shapes the app can draw; {kept}.");
            return null;
        }
        return new CycleImage(box, shapes);
    }

    /// <summary>
    /// A shape: one of "circle" [cx, cy, r], "ellipse" [cx, cy, rx, ry], "rect" [x, y, width, height], "line" [x1, y1, x2, y2],
    /// "polyline" or "polygon" [x, y, x, y…], or "path" (path data), with "fill" and "stroke" (colours, or "none"), "width"
    /// (the stroke's, 0 to 8) and "opacity" (0 to 1).
    /// </summary>
    private static CycleShape? ParseShape(JsonElement s, string where, Action<string> problem, Func<string, bool> colours, string stroke, string colourHelp)
    {
        if (s.ValueKind != JsonValueKind.Object)
        {
            problem($"{where} is an object with one shape; it's left out.");
            return null;
        }
        string? kind = null;
        foreach (var p in s.EnumerateObject())
        {
            if (p.Name is "circle" or "ellipse" or "rect" or "line" or "polyline" or "polygon" or "path")
            {
                if (kind != null) { problem($"{where} has more than one shape; it's left out."); return null; }
                kind = p.Name;
            }
            else if (p.Name is not ("fill" or "stroke" or "width" or "opacity"))
            {
                problem($"{where}.{p.Name} isn't something a shape has; it's left out.");
                return null;
            }
        }
        if (kind == null) { problem($"{where} needs a circle, ellipse, rect, line, polyline, polygon or path; it's left out."); return null; }
        var value = s.GetProperty(kind);
        double[]? numbers = null;
        string? path = null;
        if (kind == "path")
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= MaxPath } d || !PathData().IsMatch(d))
            {
                problem($"{where}.path is path data (its commands and numbers, starting with M) of up to {MaxPath} characters; it's left out.");
                return null;
            }
            path = d;
        }
        else
        {
            numbers = Numbers(value);
            var count = kind switch { "circle" => 3, "ellipse" or "rect" or "line" => 4, _ => -1 };
            var fits = numbers != null && (count > 0 ? numbers.Length == count : numbers.Length is >= 4 and <= MaxPoints && numbers.Length % 2 == 0);
            if (!fits || numbers!.Any(n => Math.Abs(n) > 10000))
            {
                problem($"{where}.{kind} isn't the numbers a {kind} takes; it's left out.");
                return null;
            }
        }
        string? Colour(string name, string fallback)
        {
            if (!s.TryGetProperty(name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.String && colours(v.GetString()!)) return v.GetString();
            problem($"{where}.{name} isn't a colour ({colourHelp}); \"{fallback}\" is used.");
            return fallback;
        }
        return new CycleShape(kind, numbers, path, Colour("fill", "none")!, Colour("stroke", stroke)!,
            Number(s, "width", 0, 8, 1.6, (w) => problem($"{where}.{w}")), Number(s, "opacity", 0, 1, 1, (w) => problem($"{where}.{w}")));
    }

    private static double[]? Numbers(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array && e.EnumerateArray().All(n => n.ValueKind == JsonValueKind.Number && double.IsFinite(n.GetDouble()))
            ? e.EnumerateArray().Select(n => n.GetDouble()).ToArray()
            : null;

    internal static double Number(JsonElement e, string name, double min, double max, double fallback, Action<string> problem)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.GetDouble() is var n && n >= min && n <= max) return n;
        problem($"{name} is a number from {min} to {max}; {fallback} is used.");
        return fallback;
    }

    internal static int Whole(JsonElement e, string name, int min, int max, int fallback, Action<string> problem)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= min && n <= max) return n;
        problem($"{name} is a whole number from {min} to {max}; {fallback} is used.");
        return fallback;
    }
}

/// <param name="ViewBox">x, y, width and height of the picture's own coordinates; it's drawn facing right, fitted to the cells.</param>
internal sealed record CycleImage(double[] ViewBox, IReadOnlyList<CycleShape> Shapes);

/// <param name="Kind">circle, ellipse, rect, line, polyline, polygon or path.</param>
/// <param name="Numbers">The shape's numbers (all but a path).</param>
/// <param name="Path">A path's data.</param>
internal sealed record CycleShape(string Kind, double[]? Numbers, string? Path, string Fill, string Stroke, double Width, double Opacity);

/// <param name="Width">How thick the ribbon is, in pixels, 1 to 6.</param>
/// <param name="Glow">How far its glow reaches, in pixels, 0 to 20.</param>
/// <param name="Fade">How long it takes to fade, in milliseconds, 200 to 3000.</param>
/// <param name="At">How far down the row it runs, 0 (top) to 1 (bottom): where the picture's wheels are.</param>
/// <param name="Colour">Its colour (see <see cref="ThemeCycle.IsColour"/>).</param>
internal sealed record CycleTrail(double Width, double Glow, int Fade, double At, string Colour);
