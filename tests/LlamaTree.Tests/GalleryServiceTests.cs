using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LlamaTree.Gallery;
using LlamaTree.Plugins;
using LlamaTree.Themes;

namespace LlamaTree.Tests;

/// <summary>Get plugins and Get themes: reading the catalogs, and what's installed, replaced and refused.</summary>
public sealed class GalleryServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ct-gallery-" + Guid.NewGuid().ToString("N")[..8]);
    private string Plugins => Path.Combine(root, "plugins");
    private string Staging => Path.Combine(root, "downloads");
    private string Themes => Path.Combine(root, "themes");
    private string BuiltIn => Path.Combine(root, "builtin");

    public GalleryServiceTests()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(BuiltIn);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] HelloZip(string version) => Zip(
        ("hello/plugin.json", $$"""{ "title": "Hello", "version": "{{version}}" }"""),
        ("hello/index.html", "<!doctype html>"));

    private const string Sha0 = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void The_plugin_catalog_leaves_out_entries_it_cant_use()
    {
        var problems = new List<string>();
        var list = GalleryService.ParsePlugins($$"""
        { "schema": 1, "plugins": [
          { "id": "messenger", "title": "Messenger", "version": "1.0.0", "placement": "panel", "access": ["message"], "inbox": true,
            "zip": "https://github.com/x/y.zip", "sha256": "{{Sha0}}", "icon": "javascript:alert(1)" },
          { "id": "Bad Id", "version": "1.0.0", "zip": "z", "sha256": "{{Sha0}}" },
          { "id": "nohash", "version": "1.0.0", "zip": "z" },
          { "id": "messenger", "version": "2.0.0", "zip": "z", "sha256": "{{Sha0}}" }
        ] }
        """, problems);
        var m = Assert.Single(list);
        Assert.Equal(("messenger", "panel", true), (m.Id, m.Placement, m.Inbox));
        Assert.Equal(["message"], m.Access);
        Assert.Null(m.Icon);   // only an inline image is shown
        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void A_catalog_newer_than_the_app_says_so()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GalleryService.ParsePlugins("""{ "schema": 2, "plugins": [] }""", []));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void A_theme_from_the_catalog_is_read_as_the_app_reads_a_theme_and_carries_its_version()
    {
        var problems = new List<string>();
        var list = GalleryService.ParseThemes("""
        { "schema": 1, "themes": [
          { "id": "forest", "version": "1.2.0", "description": "Greens", "theme": { "name": "Forest", "base": "dark", "colors": { "chrome": "#102010", "made-up": "#fff" } } },
          { "id": "dark", "version": "1.0.0", "theme": { "name": "Dark again" } },
          { "id": "nofile", "version": "1.0.0" }
        ] }
        """, problems);
        var t = Assert.Single(list);
        Assert.Equal(("Forest", "1.2.0", "#102010"), (t.Theme.Name, t.Theme.Version, t.Theme.Colors["chrome"]));
        Assert.False(t.Theme.Colors.ContainsKey("made-up"));   // a colour this copy doesn't know is left out, as from a file
        Assert.Contains("\"version\": \"1.2.0\"", t.File);
        Assert.Equal(2, problems.Count);
    }

    [Theory]
    [InlineData("1.0.0", null, false, null, false, "install")]
    [InlineData("1.1.0", null, true, "1.0.0", false, "update")]
    [InlineData("1.0.0", null, true, "1.0.0", false, "installed")]
    [InlineData("1.0.0", null, true, "1.2.0", false, "installed")]
    [InlineData("1.0.0", null, true, null, false, "yours")]
    [InlineData("1.0.0", "999.0.0", false, null, false, "needs-app")]
    [InlineData("1.0.0", "999.0.0", true, "1.0.0", false, "installed")]   // it's here already: nothing to ask of the app
    [InlineData("1.0.0", null, true, null, true, "built-in")]
    [InlineData("1.0.0", null, true, "1.0.0", true, "built-in")]             // the app comes with this version
    [InlineData("1.1.0", null, true, "1.0.0", true, "update")]               // LlamaTree-Themes has a newer one
    [InlineData("1.1.0", "999.0.0", true, "1.0.0", true, "needs-app")]
    public void What_can_be_done_with_an_entry(string version, string? min, bool have, string? haveVersion, bool builtIn, string state) =>
        Assert.Equal(state, GalleryService.StateOf(version, min, have, haveVersion, builtIn));

    [Fact]
    public void A_plugin_is_installed_and_updated_in_place()
    {
        var v1 = HelloZip("1.0.0");
        GalleryService.InstallPluginZip(v1, "hello", Sha(v1), Plugins, Staging);
        Assert.Equal("1.0.0", Assert.Single(PluginCatalog.Load(Plugins).Plugins).Version);

        var v2 = HelloZip("1.1.0");
        GalleryService.InstallPluginZip(v2, "hello", Sha(v2), Plugins, Staging);
        Assert.Equal("1.1.0", Assert.Single(PluginCatalog.Load(Plugins).Plugins).Version);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));   // nothing left behind

        GalleryService.RemovePlugin(Plugins, "hello");
        Assert.Empty(PluginCatalog.Load(Plugins).Plugins);
    }

    [Fact]
    public void A_download_that_doesnt_match_its_checksum_isnt_unpacked()
    {
        var zip = HelloZip("1.0.0");
        var ex = Assert.Throws<InvalidOperationException>(() => GalleryService.InstallPluginZip(zip, "hello", Sha0, Plugins, Staging));
        Assert.Contains("checksum", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(Plugins, "hello")));
    }

    [Theory]
    [InlineData("hello/../evil.txt")]
    [InlineData("other/plugin.json")]
    [InlineData("evil.txt")]
    public void A_zip_with_anything_outside_the_plugins_folder_is_refused(string name)
    {
        var zip = Zip(("hello/plugin.json", """{ "version": "1.0.0" }"""), ("hello/index.html", "x"), (name, "x"));
        Assert.Throws<InvalidOperationException>(() => GalleryService.InstallPluginZip(zip, "hello", Sha(zip), Plugins, Staging));
        Assert.False(Directory.Exists(Path.Combine(Plugins, "hello")));
        Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
    }

    [Fact]
    public void A_zip_that_isnt_a_loadable_plugin_is_refused()
    {
        var zip = Zip(("hello/plugin.json", """{ "entry": "missing.html", "version": "1.0.0" }"""));
        var ex = Assert.Throws<InvalidOperationException>(() => GalleryService.InstallPluginZip(zip, "hello", Sha(zip), Plugins, Staging));
        Assert.Contains("isn't a plugin", ex.Message);
    }

    [Fact]
    public void A_plugin_of_your_own_with_the_same_id_is_left_alone()
    {
        var mine = Path.Combine(Plugins, "hello");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "plugin.json"), """{ "title": "Mine" }""");   // no version: not from Get plugins
        File.WriteAllText(Path.Combine(mine, "index.html"), "mine");
        var zip = HelloZip("1.0.0");
        Assert.Throws<InvalidOperationException>(() => GalleryService.InstallPluginZip(zip, "hello", Sha(zip), Plugins, Staging));
        Assert.Throws<InvalidOperationException>(() => GalleryService.RemovePlugin(Plugins, "hello"));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(mine, "index.html")));
    }

    [Fact]
    public void Only_github_releases_of_the_plugins_repository_are_downloaded()
    {
        Assert.True(GalleryService.AllowedZip("https://github.com/LlamachantTechnology/LlamaTree-Plugins/releases/download/hello-v1.0.0/hello-1.0.0.zip"));
        Assert.False(GalleryService.AllowedZip("https://github.com/someone/else/releases/download/hello-v1.0.0/hello-1.0.0.zip"));
        Assert.False(GalleryService.AllowedZip("http://github.com/LlamachantTechnology/LlamaTree-Plugins/releases/download/x.zip"));
        Assert.False(GalleryService.AllowedZip("https://example.com/LlamachantTechnology/LlamaTree-Plugins/releases/download/x.zip"));
    }

    private GalleryTheme Forest(string version) => GalleryService.ParseThemes(
        $$"""{ "schema": 1, "themes": [{ "id": "forest", "version": "{{version}}", "theme": { "name": "Forest", "base": "dark" } }] }""", [])[0];

    [Fact]
    public void A_theme_is_installed_updated_and_removed()
    {
        GalleryService.InstallTheme(Forest("1.0.0"), Themes, BuiltIn);
        Assert.Equal("1.0.0", Assert.Single(ThemeCatalog.Load(BuiltIn, Themes).Themes).Version);
        GalleryService.InstallTheme(Forest("1.1.0"), Themes, BuiltIn);
        Assert.Equal("1.1.0", Assert.Single(ThemeCatalog.Load(BuiltIn, Themes).Themes).Version);
        GalleryService.RemoveTheme(Themes, "forest");
        Assert.Empty(ThemeCatalog.Load(BuiltIn, Themes).Themes);
    }

    [Fact]
    public void A_theme_of_your_own_or_one_the_app_comes_with_is_left_alone()
    {
        Directory.CreateDirectory(Themes);
        File.WriteAllText(Path.Combine(Themes, "forest.json"), """{ "name": "My forest" }""");
        Assert.Throws<InvalidOperationException>(() => GalleryService.InstallTheme(Forest("1.0.0"), Themes, BuiltIn));
        Assert.Throws<InvalidOperationException>(() => GalleryService.RemoveTheme(Themes, "forest"));
        Assert.Contains("My forest", File.ReadAllText(Path.Combine(Themes, "forest.json")));

        File.Delete(Path.Combine(Themes, "forest.json"));
        File.WriteAllText(Path.Combine(BuiltIn, "forest.json"), """{ "name": "Shipped forest" }""");
        Assert.Throws<InvalidOperationException>(() => GalleryService.InstallTheme(Forest("1.0.0"), Themes, BuiltIn));
    }

    [Fact]
    public void A_theme_the_app_comes_with_is_updated_from_Get_themes()
    {
        File.WriteAllText(Path.Combine(BuiltIn, "forest.json"), """{ "name": "Shipped forest", "version": "1.0.0" }""");
        Assert.Throws<InvalidOperationException>(() => GalleryService.InstallTheme(Forest("1.0.0"), Themes, BuiltIn));   // the same one

        GalleryService.InstallTheme(Forest("1.1.0"), Themes, BuiltIn);
        var forest = Assert.Single(ThemeCatalog.Load(BuiltIn, Themes).Themes);
        Assert.Equal(("Forest", "1.1.0", true), (forest.Name, forest.Version, forest.BuiltIn));   // the newer one, still one the app comes with

        // The app updated past it: its own copy is newer, and the one from Get themes is passed over, without a word.
        File.WriteAllText(Path.Combine(BuiltIn, "forest.json"), """{ "name": "Shipped forest", "version": "1.2.0" }""");
        var (themes, problems) = ThemeCatalog.Load(BuiltIn, Themes);
        Assert.Equal(("Shipped forest", "1.2.0"), (Assert.Single(themes).Name, themes[0].Version));
        Assert.Empty(problems);

        GalleryService.RemoveTheme(Themes, "forest");   // the app's own stays
        Assert.Equal("Shipped forest", Assert.Single(ThemeCatalog.Load(BuiltIn, Themes).Themes).Name);
    }
}
