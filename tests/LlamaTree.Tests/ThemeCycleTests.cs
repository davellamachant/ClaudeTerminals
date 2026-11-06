using System.Text.Json;
using LlamaTree.Themes;

namespace LlamaTree.Tests;

/// <summary>A theme's own light cycle: only shapes, numbers and colours the app checks, never markup of the theme's own.</summary>
public sealed class ThemeCycleTests
{
    private static (ThemeInfo? Theme, List<string> Problems) Read(string cycleJson, bool lightCycle = true)
    {
        var problems = new List<string>();
        using var doc = JsonDocument.Parse($$"""{ "name": "Rider" {{(lightCycle ? ", \"cursor\": \"light-cycle\"" : "")}}, "cycle": {{cycleJson}} }""");
        return (ThemeCatalog.Parse("rider", doc.RootElement, false, "rider.json", problems), problems);
    }

    [Fact]
    public void A_cycle_of_its_own_is_its_shapes_and_options()
    {
        var (theme, problems) = Read("""
            {
              "image": { "viewBox": [0, 0, 32, 16], "shapes": [
                { "circle": [6, 11, 4], "stroke": "accent", "width": 2 },
                { "path": "M2 8 C6 2 26 2 30 8 Z", "fill": "#ff8800", "stroke": "none", "opacity": 0.8 },
                { "polygon": [10, 4, 14, 2, 18, 4] }
              ] },
              "size": 4, "offset": 12, "glow": 0,
              "trail": { "width": 3, "glow": 4, "fade": 1500, "at": 0.5, "colour": "working" }
            }
            """);
        Assert.Empty(problems);
        var c = theme!.Cycle!;
        Assert.Equal((4, 12, 0.0), (c.Size, c.Offset, c.Glow));
        Assert.Equal([0, 0, 32, 16], c.Image!.ViewBox);
        Assert.Equal(["circle", "path", "polygon"], c.Image.Shapes.Select(s => s.Kind));
        Assert.Equal(("accent", "none", 2.0), (c.Image.Shapes[0].Stroke, c.Image.Shapes[0].Fill, c.Image.Shapes[0].Width));
        Assert.Equal(("#ff8800", "none", 0.8), (c.Image.Shapes[1].Fill, c.Image.Shapes[1].Stroke, c.Image.Shapes[1].Opacity));
        Assert.Equal(new CycleTrail(3, 4, 1500, 0.5, "working"), c.Trail);
    }

    [Fact]
    public void Left_out_it_keeps_the_apps_own_and_trail_false_has_no_ribbon()
    {
        var (theme, problems) = Read("""{ "trail": false }""");
        Assert.Empty(problems);
        Assert.Equal(ThemeCycle.Default with { Trail = null }, theme!.Cycle);
    }

    [Theory]
    [InlineData("""{ "path": "<script>alert(1)</script>" }""")]          // markup
    [InlineData("""{ "path": "M0 0 L1 1\" onload=\"x" }""")]              // breaking out of an attribute
    [InlineData("""{ "path": "url(https://example.com/x.svg)" }""")]      // a link
    [InlineData("""{ "path": "L1 1" }""")]                                // doesn't start with a move
    [InlineData("""{ "circle": [1, 2] }""")]                              // too few numbers
    [InlineData("""{ "circle": [1, 2, "3"] }""")]                         // not numbers
    [InlineData("""{ "polygon": [1, 2, 3] }""")]                          // an odd count
    [InlineData("""{ "rect": [0, 0, 1e9, 1] }""")]                        // out of range
    [InlineData("""{ "image": "<svg/>" }""")]                             // not a shape
    [InlineData("""{ "circle": [1, 2, 3], "href": "x.svg" }""")]          // a link
    [InlineData("""{ "circle": [1, 2, 3], "style": "fill:url(#x)" }""")]  // a style
    [InlineData("""{ "circle": [1, 2, 3], "path": "M0 0" }""")]           // two shapes in one
    public void A_shape_that_isnt_plain_numbers_is_left_out(string shape)
    {
        var (theme, problems) = Read($$"""{ "image": { "viewBox": [0, 0, 24, 16], "shapes": [ {{shape}}, { "circle": [5, 5, 2] } ] } }""");
        Assert.Single(theme!.Cycle!.Image!.Shapes);   // the good one stays
        Assert.Contains(problems, p => p.Contains("shapes[0]"));
    }

    [Theory]
    [InlineData("url(#x)")]
    [InlineData("red; background: url(x)")]
    [InlineData("var(--x)")]
    [InlineData("no-such-colour")]
    public void A_colour_is_a_name_the_app_knows_or_a_hex(string colour)
    {
        var (theme, problems) = Read($$"""{ "image": { "viewBox": [0, 0, 24, 16], "shapes": [ { "circle": [5, 5, 2], "fill": {{JsonSerializer.Serialize(colour)}} } ] }, "trail": { "colour": {{JsonSerializer.Serialize(colour)}} } }""");
        Assert.Equal("none", theme!.Cycle!.Image!.Shapes[0].Fill);
        Assert.Equal("cursor", theme.Cycle.Trail!.Colour);
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void Numbers_out_of_their_range_keep_the_apps_own()
    {
        var (theme, problems) = Read("""{ "size": 40, "offset": -5, "glow": 99, "trail": { "width": 50, "fade": 10, "at": 2 } }""");
        var c = theme!.Cycle!;
        Assert.Equal((ThemeCycle.Default.Size, ThemeCycle.Default.Offset, ThemeCycle.Default.Glow), (c.Size, c.Offset, c.Glow));
        Assert.Equal((ThemeCycle.DefaultTrail.Width, ThemeCycle.DefaultTrail.Fade, ThemeCycle.DefaultTrail.At), (c.Trail!.Width, c.Trail.Fade, c.Trail.At));
        Assert.Equal(6, problems.Count);
    }

    [Fact]
    public void Too_many_shapes_are_cut_off()
    {
        var shapes = string.Join(",", Enumerable.Range(0, ThemeCycle.MaxShapes + 5).Select(i => $$"""{ "circle": [{{i}}, 1, 1] }"""));
        var (theme, problems) = Read($$"""{ "image": { "viewBox": [0, 0, 100, 16], "shapes": [{{shapes}}] } }""");
        Assert.Equal(ThemeCycle.MaxShapes, theme!.Cycle!.Image!.Shapes.Count);
        Assert.Contains(problems, p => p.Contains($"more than {ThemeCycle.MaxShapes}"));
    }

    [Fact]
    public void A_cycle_needs_the_light_cycle()
    {
        var (theme, problems) = Read("""{ "size": 2 }""", lightCycle: false);
        Assert.Null(theme!.Cycle);
        Assert.Contains(problems, p => p.Contains("light-cycle"));
    }
}
