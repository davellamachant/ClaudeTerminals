using System.Text.Json;
using LlamaTree.Themes;

namespace LlamaTree.Tests;

/// <summary>A theme's own status dots: pictures the app checks and draws, a motion from its list, and nothing else.</summary>
public sealed class ThemeDotsTests
{
    private static (ThemeInfo? Theme, List<string> Problems) Read(string dotsJson)
    {
        var problems = new List<string>();
        using var doc = JsonDocument.Parse($$"""{ "name": "Forest", "dots": {{dotsJson}} }""");
        return (ThemeCatalog.Parse("forest", doc.RootElement, false, "forest.json", problems), problems);
    }

    private const string Square = """{ "viewBox": [0, 0, 10, 10], "shapes": [{ "rect": [1, 1, 8, 8], "fill": "status", "stroke": "none" }] }""";

    [Fact]
    public void Dots_of_its_own_are_a_picture_and_a_motion_for_each_state_it_draws()
    {
        var (theme, problems) = Read($$"""
            {
              "about": "Squares and rings.", "size": 12,
              "working": { "image": { "viewBox": [0, 0, 10, 10], "shapes": [{ "path": "M5 1 A4 4 0 1 1 1 5" }] }, "motion": "spin" },
              "waiting": { "image": {{Square}}, "motion": "blink" },
              "stopped": { "image": { "viewBox": [0, 0, 10, 10], "shapes": [{ "circle": [5, 5, 4], "stroke": "text-faint", "fill": "#ffffff22" }] } }
            }
            """);
        Assert.Empty(problems);
        var d = theme!.Dots!;
        Assert.Equal(["working", "waiting", "stopped"], d.States.Keys);
        Assert.Equal(12, d.Size);
        Assert.Equal("Squares and rings.", d.About);
        Assert.Equal("spin", d.States["working"].Motion);
        Assert.Equal("none", d.States["stopped"].Motion);   // a dot that doesn't say stands still
        var arc = d.States["working"].Image.Shapes[0];
        Assert.Equal(("path", "status", "none"), (arc.Kind, arc.Stroke, arc.Fill));   // stroked in its state's colour unless it says
        Assert.Equal("status", d.States["waiting"].Image.Shapes[0].Fill);
    }

    [Fact]
    public void A_theme_without_dots_has_none()
    {
        var problems = new List<string>();
        using var doc = JsonDocument.Parse("""{ "name": "Plain" }""");
        Assert.Null(ThemeCatalog.Parse("plain", doc.RootElement, false, "plain.json", problems)!.Dots);
        Assert.Empty(problems);
    }

    [Fact]
    public void What_isnt_valid_is_left_out_with_why()
    {
        var (theme, problems) = Read($$"""
            {
              "size": 40, "about": "",
              "working": { "image": {{Square}}, "motion": "wobble" },
              "thinking": { "image": {{Square}} },
              "asking": { "motion": "hop" },
              "idle": { "image": { "viewBox": [0, 0, 10, 10], "shapes": [{ "rect": [1, 1, 8, 8], "fill": "cursor" }, { "script": "alert(1)" }] } }
            }
            """);
        var d = theme!.Dots!;
        Assert.Equal(ThemeDots.DefaultSize, d.Size);
        Assert.Null(d.About);
        Assert.Equal("none", d.States["working"].Motion);
        Assert.False(d.States.ContainsKey("thinking"));
        Assert.False(d.States.ContainsKey("asking"));   // no picture: a plain dot
        var idle = Assert.Single(d.States["idle"].Image.Shapes);   // the script isn't a shape
        Assert.Equal("none", idle.Fill);   // "cursor" means nothing to a dot
        Assert.Contains(problems, p => p.Contains("dots.size"));
        Assert.Contains(problems, p => p.Contains("dots.about"));
        Assert.Contains(problems, p => p.Contains("dots.working.motion"));
        Assert.Contains(problems, p => p.Contains("dots.thinking isn't a state"));
        Assert.Contains(problems, p => p.Contains("dots.asking"));
        Assert.Contains(problems, p => p.Contains("dots.idle.image.shapes[0].fill"));
        Assert.Contains(problems, p => p.Contains("dots.idle.image.shapes[1]"));
    }

    [Fact]
    public void Dots_with_nothing_to_draw_are_none()
    {
        var (theme, problems) = Read("""{ "working": { "image": { "viewBox": [0, 0, 10, 10], "shapes": [] } } }""");
        Assert.Null(theme!.Dots);
        Assert.Contains(problems, p => p.Contains("keeps the usual dots"));
    }

    [Fact]
    public void Status_colours_of_its_own_are_page_colours()
    {
        var problems = new List<string>();
        using var doc = JsonDocument.Parse("""{ "name": "Forest", "colors": { "status-working": "#3fe0d0", "status-asking": "rgb(240, 140, 190)" } }""");
        var theme = ThemeCatalog.Parse("forest", doc.RootElement, false, "forest.json", problems)!;
        Assert.Empty(problems);
        Assert.Equal("#3fe0d0", theme.Colors["status-working"]);
        Assert.Equal("rgb(240, 140, 190)", theme.Colors["status-asking"]);
    }

    [Fact]
    public void A_saved_theme_keeps_its_dots()
    {
        var folder = Path.Combine(Path.GetTempPath(), "llamatree-dots-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var doc = JsonDocument.Parse($$"""{ "name": "Mine", "base": "dark", "dots": { "size": 9, "waiting": { "image": {{Square}}, "motion": "pulse" } } }""");
            var id = ThemeCatalog.Save(folder, Path.Combine(folder, "none"), doc.RootElement);
            var (themes, problems) = ThemeCatalog.Load(Path.Combine(folder, "none"), folder);
            Assert.Empty(problems);
            var dots = themes.Single(t => t.Id == id).Dots!;
            Assert.Equal(9, dots.Size);
            Assert.Equal("pulse", dots.States["waiting"].Motion);
        }
        finally { Directory.Delete(folder, true); }
    }
}
