using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeTerminals.Themes;

namespace ClaudeTerminals.Tests;

/// <summary>Theme files: the app's own, and what a theme of your own can and can't set.</summary>
public sealed class ThemeCatalogTests : IDisposable
{
    private readonly string builtIn = Path.Combine(Path.GetTempPath(), "ct-themes-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string user = Path.Combine(Path.GetTempPath(), "ct-mythemes-" + Guid.NewGuid().ToString("N")[..8]);

    // The app's own files, as the build copies them next to the tests.
    private static string Shipped(string path) => Path.Combine(AppContext.BaseDirectory, "wwwroot", path);

    public ThemeCatalogTests()
    {
        Directory.CreateDirectory(builtIn);
        Directory.CreateDirectory(user);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { builtIn, user })
            try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private void Write(string folder, string file, object theme) => File.WriteAllText(Path.Combine(folder, file), JsonSerializer.Serialize(theme));

    [Fact]
    public void The_apps_own_themes_load()
    {
        var (themes, problems) = ThemeCatalog.Load(Shipped("themes"), user);

        Assert.Empty(problems);
        var nerds = Assert.Single(themes, t => t.Id == "nerds");
        // Built from the accent colour: no colours, or accent, of its own.
        Assert.Equal(("Nerds", "dark", "Cascadia Mono", true, true, true), (nerds.Name, nerds.Base, nerds.Font, nerds.Glow, nerds.Monochrome, nerds.BuiltIn));
        Assert.Null(nerds.Accent);
        Assert.Empty(nerds.Colors);
    }

    [Fact]
    public void Every_colour_a_theme_can_set_is_one_the_stylesheet_uses()
    {
        var css = File.ReadAllText(Shipped("app.css"));
        var root = css[css.IndexOf(":root {", StringComparison.Ordinal)..];
        root = root[..root.IndexOf('}')];
        var defined = Regex.Matches(root, @"--([a-z0-9-]+):").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Subset(defined, ThemeCatalog.ColorNames.ToHashSet());
        // And every colour it defines is one a theme can set, apart from the accent (a theme's "accent") and layout.
        Assert.Empty(defined.Except(ThemeCatalog.ColorNames).Except(["accent", "accent-strong", "header-h"]));
    }

    [Fact]
    public void A_theme_of_your_own_only_keeps_what_it_may_set()
    {
        Write(user, "Neon.json", new
        {
            name = "Neon",
            @base = "light",
            font = "Comic Sans; } body { display: none",
            colors = new { chrome = "#101010", text = "rgba(255, 0, 255, .8)", accent = "#ff00ff", sneaky = "#000", panel = "url(https://example.com/x.png)" },
            terminal = new { red = "#ff0000", fontSize = "40" },
            diff = new { insertedLine = "rgb(0, 255, 0)", removedLine = "#ff000044" },
        });

        var (themes, problems) = ThemeCatalog.Load(builtIn, user);

        var neon = Assert.Single(themes);
        Assert.Equal(("neon", "light", null, false), (neon.Id, neon.Base, neon.Font, neon.BuiltIn));
        Assert.Equal(new Dictionary<string, string> { ["chrome"] = "#101010", ["text"] = "rgba(255, 0, 255, .8)" }, neon.Colors);
        Assert.Equal(new Dictionary<string, string> { ["red"] = "#ff0000" }, neon.Terminal);
        Assert.Equal(new Dictionary<string, string> { ["removedLine"] = "#ff000044" }, neon.Diff);   // the editor only takes hex
        Assert.Equal(6, problems.Count);   // the font, colors.accent and .sneaky, panel's url, fontSize, and the diff's rgb()
        Assert.All(problems, p => Assert.StartsWith("Neon.json: ", p));
    }

    [Fact]
    public void A_theme_can_take_neither_the_menus_own_names_nor_another_themes()
    {
        Write(builtIn, "matrix.json", new { name = "Matrix" });
        Write(user, "matrix.json", new { name = "My Matrix" });
        Write(user, "dark.json", new { name = "Darker" });
        Write(user, "bad name.json", new { name = "Spaces" });
        File.WriteAllText(Path.Combine(user, "broken.json"), "{ not json");

        var (themes, problems) = ThemeCatalog.Load(builtIn, user);

        Assert.Equal("Matrix", Assert.Single(themes).Name);
        Assert.Equal(4, problems.Count);
    }

    [Fact]
    public void A_saved_theme_gets_a_file_of_its_own_and_loads_as_it_was()
    {
        Write(builtIn, "nerds.json", new { name = "Nerds" });
        var theme = JsonSerializer.SerializeToElement(new
        {
            name = "My Nerds!",
            @base = "dark",
            font = "Cascadia Mono",
            accent = new { line = "#39ff6a", strong = "#0b6b26" },
            colors = new { chrome = "#050a06", text = "#8cf0a6" },
            terminal = new { background = "#020703" },
            diff = new { insertedLine = "#1a5a2a55" },
        });

        Assert.Equal("my-nerds", ThemeCatalog.Save(user, builtIn, theme));
        Assert.Equal("my-nerds-2", ThemeCatalog.Save(user, builtIn, theme));   // never over another

        var (themes, problems) = ThemeCatalog.Load(builtIn, user);
        Assert.Empty(problems);
        var saved = Assert.Single(themes, t => t.Id == "my-nerds");
        Assert.Equal(("My Nerds!", "Cascadia Mono", "#8cf0a6", "#020703", "#1a5a2a55"),
            (saved.Name, saved.Font, saved.Colors["text"], saved.Terminal["background"], saved.Diff["insertedLine"]));
    }

    [Theory]
    [InlineData("Nerds", "nerds-2")]   // a built-in theme's name
    [InlineData("Dark", "my-theme")]   // one of the menu's own
    [InlineData("!!!", "my-theme")]
    public void A_saved_theme_never_takes_a_name_in_use(string name, string expected)
    {
        Write(builtIn, "nerds.json", new { name = "Nerds" });
        Assert.Equal(expected, ThemeCatalog.Save(user, builtIn, JsonSerializer.SerializeToElement(new { name, @base = "dark" })));
    }

    [Fact]
    public void A_theme_with_anything_it_cant_use_isnt_saved()
    {
        var bad = JsonSerializer.SerializeToElement(new { name = "Bad", @base = "dark", colors = new { chrome = "url(x)" } });
        Assert.Throws<InvalidOperationException>(() => ThemeCatalog.Save(user, builtIn, bad));
        Assert.Empty(Directory.GetFiles(user));
    }

    [Fact]
    public void A_base_other_than_light_or_dark_leaves_the_theme_out()
    {
        Write(user, "odd.json", new { name = "Odd", @base = "sepia" });
        var (themes, problems) = ThemeCatalog.Load(builtIn, user);
        Assert.Empty(themes);
        Assert.Contains("base", Assert.Single(problems));
    }
}
