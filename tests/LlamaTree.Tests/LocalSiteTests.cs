using System.Text.RegularExpressions;
using LlamaTree.Hosting;
using LlamaTree.Plugins;

namespace LlamaTree.Tests;

/// <summary>
/// The app page's Content-Security-Policy: its frames load only plugins' pages, so a plugin's frame that follows a link
/// elsewhere is stopped rather than the page it lands on speaking for the plugin. The Windows window has it from the
/// page's meta tag; the Photino window's local site serves its own in place of that.
/// </summary>
public class LocalSiteTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lt-site-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly string AppIndex = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");

    public LocalSiteTests()
    {
        Directory.CreateDirectory(root);
        File.Copy(AppIndex, Path.Combine(root, "index.html"));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    /// <summary>The page's own policy (the meta tag's content), or null.</summary>
    private static string? MetaPolicy(string html) =>
        Regex.Match(html, """<meta\s+http-equiv="Content-Security-Policy"\s+content="([^"]*)">""", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Groups[1].Value
            : null;

    private static string? Directive(string policy, string name) =>
        policy.Split(';').Select(d => d.Trim()).FirstOrDefault(d => d.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..];

    [Fact]
    public void The_app_page_frames_only_plugins_on_Windows()
    {
        var policy = MetaPolicy(File.ReadAllText(AppIndex));
        Assert.NotNull(policy);
        // Each plugin at https://<id>.plugin.local/ (see PluginCatalog.HostFor), and nothing else.
        Assert.Equal("https://*" + PluginCatalog.HostSuffix, Directive(policy, "frame-src"));
        Assert.Equal("'none'", Directive(policy, "object-src"));
        Assert.Equal("'none'", Directive(policy, "base-uri"));
    }

    [Fact]
    public async Task The_local_site_serves_the_app_page_with_its_own_policy()
    {
        using var site = LocalSite.Start(root, "token123");
        using var http = new HttpClient();
        using var response = await http.GetAsync(site.AppPage);
        response.EnsureSuccessStatusCode();
        var policy = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal($"{site.Origin}/plugin/", Directive(policy, "frame-src"));
        Assert.Equal("'none'", Directive(policy, "frame-ancestors"));
        Assert.Equal("'none'", Directive(policy, "object-src"));
        Assert.Equal("'none'", Directive(policy, "base-uri"));
        Assert.StartsWith(Directive(policy, "frame-src")!, site.PluginBase("hello"));   // a plugin's address is allowed

        // The Windows policy is taken out: both would apply, and it would block every plugin here.
        var html = await response.Content.ReadAsStringAsync();
        Assert.Null(MetaPolicy(html));
        Assert.DoesNotContain("Content-Security-Policy", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<meta name=\"llamatree-bridge\" content=\"token123\">", html);
    }

    [Fact]
    public void The_local_site_policy_is_for_its_own_origin()
    {
        Assert.Equal("frame-ancestors 'none'; frame-src http://127.0.0.1:5000/plugin/; object-src 'none'; base-uri 'none'",
            LocalSite.AppPagePolicy("http://127.0.0.1:5000"));
    }
}

/// <summary>The Photino window's local web server serving plugins (see <see cref="LocalSite"/>).</summary>
public class LocalSitePluginTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lt-site-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly LocalSite site;
    private readonly HttpClient http = new();

    public LocalSitePluginTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), "<html><head></head><body>app</body></html>");
        File.WriteAllText(Path.Combine(root, "wwwroot", "app.js"), "// app");
        Directory.CreateDirectory(Path.Combine(root, "plugin", "sub"));
        File.WriteAllText(Path.Combine(root, "plugin", "index.html"), "<html>plugin</html>");
        File.WriteAllText(Path.Combine(root, "plugin", "sub", "main.js"), "// plugin");
        site = LocalSite.Start(Path.Combine(root, "wwwroot"), "token");
        site.ServePlugin("sample", Path.Combine(root, "plugin"));
    }

    public void Dispose()
    {
        http.Dispose();
        site.Dispose();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private static string? Policy(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Permissions-Policy", out var v) ? string.Join(",", v) : null;

    [Theory]
    [InlineData("")]
    [InlineData("index.html")]
    [InlineData("sub/main.js")]
    public async Task Every_plugin_page_and_file_is_kept_off_the_clipboard(string file)
    {
        using var r = await http.GetAsync(site.PluginBase("sample") + file);
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("clipboard-read=(), clipboard-write=()", Policy(r));
    }

    [Fact]
    public async Task The_app_page_keeps_the_clipboard()
    {
        using var page = await http.GetAsync(site.AppPage);
        Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("llamatree-bridge", await page.Content.ReadAsStringAsync());
        Assert.Null(Policy(page));
        using var script = await http.GetAsync(site.AppPage.Replace("index.html", "app.js"));
        Assert.Equal(System.Net.HttpStatusCode.OK, script.StatusCode);
        Assert.Null(Policy(script));
    }
}
