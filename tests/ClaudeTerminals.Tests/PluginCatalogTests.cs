using ClaudeTerminals.Plugins;

namespace ClaudeTerminals.Tests;

/// <summary>Finding plugins in the profile's plugins folder, and what keeps one out.</summary>
public class PluginCatalogTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ct-plugins-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginCatalogTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (DirectoryNotFoundException) { }
    }

    private void Plugin(string name, string? manifest, params string[] files)
    {
        var dir = Path.Combine(folder, name);
        Directory.CreateDirectory(dir);
        if (manifest != null) File.WriteAllText(Path.Combine(dir, PluginCatalog.Manifest), manifest);
        foreach (var f in files)
        {
            var path = Path.Combine(dir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "<!doctype html>");
        }
    }

    [Fact]
    public void A_plugin_is_a_folder_with_a_manifest_and_its_page()
    {
        Plugin("Floor-Plan", """{ "title": "Floor plan", "description": "Sessions on a map", "entry": "app/main.html" }""", "app/main.html");
        Plugin("hello", "{}", "index.html");
        Plugin("not-a-plugin", null, "index.html");   // no manifest: just a folder
        var (plugins, problems) = PluginCatalog.Load(folder);
        Assert.Empty(problems);
        Assert.Equal(["Floor plan", "hello"], plugins.Select(p => p.Title));
        var floor = plugins[0];
        Assert.Equal("floor-plan", floor.Id);
        Assert.Equal("https://floor-plan.plugin.local/app/main.html", floor.Url);
        Assert.Equal("Sessions on a map", floor.Description);
        Assert.Equal("https://hello.plugin.local/index.html", plugins[1].Url);   // the folder's name and index.html by default
    }

    [Fact]
    public void A_plugin_whose_page_is_outside_its_folder_or_missing_is_left_out()
    {
        Plugin("escape", """{ "entry": "../other/index.html" }""");
        Plugin("rooted", """{ "entry": "C:\\Windows\\win.ini" }""");
        Plugin("missing", """{ "entry": "nothing.html" }""");
        Plugin("bad-json", "{", "index.html");
        Plugin("under_score", "{}", "index.html");
        var (plugins, problems) = PluginCatalog.Load(folder);
        Assert.Empty(plugins);
        Assert.Equal(5, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("under_score:") && p.Contains("letters, digits and hyphens"));
        Assert.Contains(problems, p => p.StartsWith("escape:") && p.Contains("inside its folder"));
    }

    [Fact]
    public void A_plugin_can_show_in_the_side_panel_and_add_menu_items()
    {
        Plugin("counter", """
            { "placement": "Panel", "menu": [
                { "label": "Count", "command": "add" },
                { "label": "", "command": "no label" },
                { "label": "No command" },
                { "label": "Reset", "command": "reset" } ] }
            """, "index.html");
        Plugin("plain", "{}", "index.html");
        var (plugins, problems) = PluginCatalog.Load(folder);
        Assert.Equal(2, problems.Count(p => p.StartsWith("counter:") && p.Contains("needs a label")));   // and it says so
        var counter = plugins.Single(p => p.Id == "counter");
        Assert.Equal("panel", counter.Placement);
        Assert.Equal([("Count", "add"), ("Reset", "reset")], counter.Menu.Select(m => (m.Label, m.Command)));   // items missing a part are left out
        Assert.Equal("tab", plugins.Single(p => p.Id == "plain").Placement);
        Assert.Empty(plugins.Single(p => p.Id == "plain").Menu);
    }

    [Fact]
    public void A_plugin_asks_for_eight_menu_items_at_most_and_a_known_placement()
    {
        var items = string.Join(",", Enumerable.Range(1, 12).Select(i => $$"""{ "label": "Item {{i}}", "command": "c{{i}}" }"""));
        Plugin("many", $$"""{ "menu": [{{items}}] }""", "index.html");
        Plugin("floating", """{ "placement": "window" }""", "index.html");
        var (plugins, problems) = PluginCatalog.Load(folder);
        Assert.Equal(PluginCatalog.MaxMenuItems, plugins.Single().Menu.Count);
        Assert.Contains(problems, p => p.StartsWith("many:") && p.Contains("first 8"));
        Assert.Contains(problems, p => p.StartsWith("floating:") && p.Contains("placement"));
    }

    [Fact]
    public void A_menu_item_names_the_menus_it_is_on()
    {
        Plugin("map", """
            { "menu": [
                { "label": "Palette", "command": "a" },
                { "label": "Sessions", "command": "b", "where": ["session", "SessionTab"] },
                { "label": "Project", "command": "c", "where": "project" },
                { "label": "Partly", "command": "d", "where": ["session", "terminal"] },
                { "label": "Nowhere", "command": "e", "where": ["terminal"] } ] }
            """, "index.html");
        var (plugins, problems) = PluginCatalog.Load(folder);
        Assert.Equal(
            [("Palette", "palette"), ("Sessions", "session,sessionTab"), ("Project", "project"), ("Partly", "session")],
            plugins.Single().Menu.Select(m => (m.Label, string.Join(",", m.Where))));
        Assert.Contains(problems, p => p.Contains("\"Partly\" is on a menu there isn't"));
        Assert.Contains(problems, p => p.Contains("\"Nowhere\" is on no menu there is"));
    }

    [Fact]
    public void A_plugin_names_what_it_asks_to_see()
    {
        Plugin("floor", """{ "access": ["Sessions", "files"] }""", "index.html");
        Plugin("one", """{ "access": "sessions" }""", "index.html");
        Plugin("plain", "{}", "index.html");
        var (plugins, problems) = PluginCatalog.Load(folder);
        Assert.Equal(["sessions"], plugins.Single(p => p.Id == "floor").Access);
        Assert.Contains(problems, p => p.StartsWith("floor:") && p.Contains("\"files\", which there isn't"));
        Assert.Equal(["sessions"], plugins.Single(p => p.Id == "one").Access);
        Assert.Empty(plugins.Single(p => p.Id == "plain").Access);   // nothing beyond its own page
    }

    [Fact]
    public void A_title_bar_button_is_an_icon_from_the_plugins_folder()
    {
        Plugin("map", """{ "toolbar": { "icon": "img/map.svg", "tooltip": "Open the map", "command": "open" } }""", "index.html", "img/map.svg");
        Plugin("plain", """{ "toolbar": { "icon": "icon.png" } }""", "index.html", "icon.png");
        Plugin("no-icon", """{ "toolbar": { "tooltip": "Text only" } }""", "index.html");
        Plugin("script-icon", """{ "toolbar": { "icon": "index.html" } }""", "index.html");
        Plugin("far-icon", """{ "toolbar": { "icon": "../map/img/map.svg" } }""", "index.html");
        var (plugins, problems) = PluginCatalog.Load(folder);

        var map = plugins.Single(p => p.Id == "map").Toolbar!;
        Assert.StartsWith("data:image/svg+xml;base64,", map.Icon);   // the image itself: the page can't load it from the plugin's address
        Assert.Equal("<!doctype html>", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(map.Icon.Split(',')[1])));
        Assert.Equal(("Open the map", "open"), (map.Tooltip, map.Command));
        Assert.Equal((null, null), (plugins.Single(p => p.Id == "plain").Toolbar!.Tooltip, plugins.Single(p => p.Id == "plain").Toolbar!.Command));

        // Without a usable icon the plugin still loads, without its button, and says why.
        foreach (var id in new[] { "no-icon", "script-icon", "far-icon" })
        {
            Assert.Null(plugins.Single(p => p.Id == id).Toolbar);
            Assert.Contains(problems, p => p.StartsWith(id + ":") && p.Contains("needs an icon"));
        }
    }

    [Fact]
    public void A_title_bar_icon_over_64_KB_is_refused()
    {
        Plugin("big", """{ "toolbar": { "icon": "big.png" } }""", "index.html");
        File.WriteAllBytes(Path.Combine(folder, "big", "big.png"), new byte[PluginCatalog.MaxIconBytes + 1]);
        var (plugins, problems) = PluginCatalog.Load(folder);
        Assert.Null(plugins.Single().Toolbar);
        Assert.Contains(problems, p => p.Contains("over 64 KB"));
    }

    [Theory]
    [InlineData("https://hello.plugin.local/index.html", true)]
    [InlineData("https://app.local/index.html", false)]
    [InlineData("http://hello.plugin.local/index.html", false)]
    [InlineData("https://plugin.local.example.com/", false)]
    [InlineData("about:blank", false)]
    public void Plugin_addresses_are_told_apart(string uri, bool plugin) => Assert.Equal(plugin, PluginCatalog.IsPluginUri(uri));

    [Fact]
    public void No_plugins_folder_means_no_plugins() =>
        Assert.Empty(PluginCatalog.Load(Path.Combine(folder, "none")).Plugins);

    [Fact]
    public void A_plugin_can_ask_for_asking_and_typing_too()
    {
        Plugin("ask", """{ "access": ["type", "ask", "sessions"] }""", "index.html");
        var (plugins, problems) = PluginCatalog.Load(folder);
        Assert.Empty(problems);
        Assert.Equal(["sessions", "ask", "type"], plugins.Single().Access);   // in the catalog's order
    }
}
