using LlamaTree.Hosting;

namespace LlamaTree.Tests;

/// <summary>The Photino window's local web server (see <see cref="LocalSite"/>).</summary>
public class LocalSiteTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lt-site-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly LocalSite site;
    private readonly HttpClient http = new();

    public LocalSiteTests()
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
