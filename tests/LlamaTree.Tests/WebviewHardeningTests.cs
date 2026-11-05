using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaTree.Hosting;
using LlamaTree.Hosting.Link;
using LlamaTree.Plugins;

namespace LlamaTree.Tests;

/// <summary>What a --remote window takes from a server (colours, plugins' addresses), and which browser permissions the window gives.</summary>
public sealed class WebviewHardeningTests
{
    // ---------------------------------------------------------------- a server's colours

    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#abcd", true)]
    [InlineData("#d97757", true)]
    [InlineData("#D97757ff", true)]
    [InlineData("#ab", false)]
    [InlineData("#abcde", false)]
    [InlineData("#abcdefg", false)]
    [InlineData("red", false)]
    [InlineData("#fff; background: url(https://example.com/x)", false)]
    [InlineData("#fff\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_hex_colour_is_a_colour(string? color, bool ok) => Assert.Equal(ok, ServerColors.IsColor(color));

    [Fact]
    public void A_servers_agent_and_project_colours_that_arent_colours_are_taken_out()
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "sessions",
            agents = new object[]
            {
                new { id = "claude", color = new { dark = "#d97757", light = "#c15f3c" } },
                new { id = "evil", color = new { dark = "red;background:url(https://example.com/)", light = "#fff" } },
                new { id = "odd", color = (object)"#fff" },
                new { id = "plain", color = (object?)null },
            },
            projects = new object[]
            {
                new { id = "a", color = (string?)"#4f9cf5" },
                new { id = "b", color = (string?)"#000;--x:url(x)" },
                new { id = "c", color = (string?)null },
            },
            sessions = Array.Empty<object>(),
        });
        var m = JsonNode.Parse(ServerColors.Sanitize(json))!;
        Assert.Equal("#d97757", (string?)m["agents"]![0]!["color"]!["dark"]);
        Assert.Null(m["agents"]![1]!["color"]);
        Assert.Null(m["agents"]![2]!["color"]);
        Assert.Null(m["agents"]![3]!["color"]);
        Assert.Equal("#4f9cf5", (string?)m["projects"]![0]!["color"]);
        Assert.Null(m["projects"]![1]!["color"]);
        Assert.Null(m["projects"]![2]!["color"]);
    }

    [Theory]
    [InlineData("""{"type":"sessions","agents":[{"id":"claude","color":{"dark":"#d97757","light":"#c15f3c"}}],"projects":[{"id":"a","color":"#4f9cf5"}]}""")]
    [InlineData("""{"type":"output","id":"x","data":"\"sessions\" color: red;"}""")]   // not the session list
    [InlineData("""{"type":"sessions",""")]                                              // not JSON
    [InlineData("""{"type":"output","data":"plain"}""")]
    public void Other_messages_and_good_colours_go_as_they_came(string json) => Assert.Same(json, ServerColors.Sanitize(json));

    // ---------------------------------------------------------------- a server's plugins in a --remote window

    [Fact]
    public void A_servers_plugin_never_gets_a_local_plugins_id_or_address()
    {
        var id = PluginAddressing.RemoteId("dev@server", "notes");
        Assert.NotEqual("notes", id);
        Assert.StartsWith("r-", id);
        Assert.Equal(50, id.Length);
        Assert.False(PluginCatalog.IsValidId(id));   // longer than any plugin folder here can be named
        Assert.NotEqual(AppHost.ServerPluginId("dev@server", "notes"), id);   // nor a connected server's plugin's here
        Assert.Matches("^[a-z0-9-]{1,63}$", id);   // still a host name label, for the Windows window's virtual host
        Assert.Equal(id, PluginAddressing.RemoteId("DEV@Server", "notes"));
        Assert.NotEqual(id, PluginAddressing.RemoteId("dev@other", "notes"));
        Assert.NotEqual(id, PluginAddressing.RemoteId("dev@server", "board"));

        var remote = new PluginAddressing(null, null, "dev@server");
        Assert.Equal($"https://{id}.plugin.local/", remote.Base("notes"));
        Assert.NotEqual(PluginAddressing.VirtualHosts.Base("notes"), remote.Base("notes"));
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var site = new PluginAddressing("http://127.0.0.1:5000", key, "dev@server");
        Assert.Equal($"http://127.0.0.1:5000/plugin/{PluginAddressing.Segment(key, id)}/", site.Base("notes"));
        Assert.NotEqual(new PluginAddressing("http://127.0.0.1:5000", key).Base("notes"), site.Base("notes"));
    }

    [Fact]
    public void The_host_names_plugins_as_the_window_serves_them()
    {
        static PluginAddressing From(string hello, string? remote)
        {
            using var doc = JsonDocument.Parse(hello);
            return SessionHost.PluginsFrom(doc.RootElement, remote);
        }
        // This computer's window.
        Assert.Same(PluginAddressing.VirtualHosts, From("""{"pid":1,"plugins":{"origin":null,"key":null,"namespaced":false}}""", null));
        Assert.Equal(new PluginAddressing("http://127.0.0.1:1", "00"), From("""{"plugins":{"origin":"http://127.0.0.1:1","key":"00"}}""", null));
        // Namespaced only for a --remote window that says so; a window can't pick the server name it's namespaced by.
        Assert.Same(PluginAddressing.VirtualHosts, From("""{"plugins":{"namespaced":true}}""", null));
        Assert.Equal(new PluginAddressing(null, null, "dev@server"), From("""{"plugins":{"origin":null,"key":null,"namespaced":true},"remote":"dev@server"}""", "dev@server"));
        Assert.Equal(new PluginAddressing("http://127.0.0.1:1", "00", "dev@server"), From("""{"plugins":{"origin":"http://127.0.0.1:1","key":"00","namespaced":true}}""", "dev@server"));
        // An older window, which serves them under their own ids.
        Assert.Same(PluginAddressing.VirtualHosts, From("""{"plugins":{"origin":null,"key":null}}""", "dev@server"));
        // Another computer's host, which serves no plugins.
        Assert.Same(PluginAddressing.VirtualHosts, From("""{"remote":"dev@server"}""", "dev@server"));
    }

    // ---------------------------------------------------------------- browser permissions (the Windows window)

    [Theory]
    [InlineData(true, "https://app.local/", false, "Allow")]
    [InlineData(true, "https://APP.LOCAL", false, "Allow")]
    [InlineData(false, "https://app.local/", false, "Default")]   // the app page's other permissions: you're asked
    [InlineData(true, "https://app.local/", true, "Deny")]        // a frame naming the app's origin is still a frame
    [InlineData(true, "https://notes.plugin.local/", false, "Deny")]
    [InlineData(true, "https://notes.plugin.local/", true, "Deny")]
    [InlineData(false, "https://notes.plugin.local/", true, "Deny")]
    [InlineData(true, "http://app.local/", false, "Deny")]
    [InlineData(true, "https://app.local:8443/", false, "Deny")]
    [InlineData(true, "https://app.local.example.com/", false, "Deny")]
    [InlineData(true, "https://example.com/", false, "Deny")]
    [InlineData(true, "null", false, "Deny")]
    [InlineData(true, null, false, "Deny")]
    public void Only_the_app_page_reads_the_clipboard(bool clipboardRead, string? uri, bool fromFrame, string answer) =>
        Assert.Equal(answer, WebPermissions.Decide(clipboardRead, uri, fromFrame).ToString());
}
