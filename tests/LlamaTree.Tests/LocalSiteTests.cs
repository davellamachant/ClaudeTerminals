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
