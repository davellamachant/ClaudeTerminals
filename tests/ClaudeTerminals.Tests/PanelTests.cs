using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeTerminals.Panels;
using ClaudeTerminals.Sessions;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Tests;

public class PanelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ct-panels-" + Guid.NewGuid().ToString("N"));
    private string StoreFile => Path.Combine(folder, "panels.json");

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (DirectoryNotFoundException) { }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ---- the command line

    [Fact]
    public void Claude_is_connected_to_the_panel_server_and_its_tools_are_allowed() =>
        Assert.Equal("claude --continue --mcp-config \"C:\\a b\\mcp.json\" --allowedTools mcp__claude-terminals",
            TerminalSession.PrepareCommand("claude --continue", null, () => true, mcpConfig: "C:\\a b\\mcp.json"));

    [Theory]
    [InlineData("claude --mcp-config mine.json")]   // the user's own servers win
    [InlineData("claude -p hello")]
    [InlineData("pwsh")]
    public void The_panel_server_is_only_added_to_an_interactive_claude_without_its_own_config(string command) =>
        Assert.Equal(command, TerminalSession.PrepareCommand(command, null, () => true, mcpConfig: "M.json"));

    [Fact]
    public void A_session_started_from_a_panel_is_a_new_conversation_starting_with_the_prompt()
    {
        var line = TerminalSession.PrepareCommand("claude --continue --model opus", "abc-123", () => true, "T.json", "M.json",
            newConversation: true, firstPrompt: "Work on \"LT-475\"");
        // The prompt comes first: --mcp-config and --allowedTools take every argument after them.
        Assert.Equal("claude \"Work on \\\"LT-475\\\"\" --model opus --settings \"T.json\" --mcp-config \"M.json\" --allowedTools mcp__claude-terminals", line);
    }

    [Fact]
    public void A_new_conversation_without_a_prompt_on_the_command_line_still_drops_continue() =>
        Assert.Equal("claude", TerminalSession.PrepareCommand("claude --continue", null, () => true, newConversation: true));

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir\", "\"C:\\dir\\\\\"")]               // trailing backslashes doubled before the closing quote
    [InlineData(@"a\""b", "\"a\\\\\\\"b\"")]                   // backslash before a quote: doubled, plus the escape
    [InlineData("--help", "\" --help\"")]                      // never read as an option
    [InlineData("100% & more", "\"100% & more\"")]
    public void Arguments_are_quoted_for_the_usual_command_line_rules(string text, string expected) =>
        Assert.Equal(expected, CommandLineResolver.QuoteArgument(text));

    // ---- the store

    private const string TasksPanel = """
        { "id": "my-tasks", "title": "My tasks", "subtitle": "LlamaTrack", "scope": "project", "refresh": "/my-tasks",
          "items": [ { "key": "LT-475", "title": "Webhook retries create duplicate orders", "link": "https://example.com/LT-475",
                       "badges": [ { "text": "Urgent", "tone": "warning" }, "To do" ],
                       "buttons": [ { "label": "Start session", "prompt": "Work on LT-475", "target": "new", "name": "LT-475" } ] } ] }
        """;

    [Fact]
    public void A_panel_is_shown_kept_and_reloaded()
    {
        var store = new PanelStore(StoreFile);
        var (panel, created) = store.Show("s1", Json(TasksPanel));
        Assert.True(created);
        Assert.Equal("project", panel.Scope);
        var item = Assert.Single(panel.Items!);
        Assert.Equal("warning", item.Badges![0].Tone);
        Assert.Equal("neutral", item.Badges[1].Tone);
        Assert.Equal("new", item.Buttons![0].Target);

        var reloaded = Assert.Single(new PanelStore(StoreFile).Snapshot());
        Assert.Equal("My tasks", reloaded.Title);
        Assert.Equal("/my-tasks", reloaded.Refresh);
        Assert.Equal("https://example.com/LT-475", reloaded.Items![0].Link);
    }

    [Fact]
    public void Showing_again_replaces_the_content_but_keeps_where_the_person_put_it()
    {
        var store = new PanelStore(StoreFile);
        store.Show("s1", Json(TasksPanel));
        store.SetScope("s1", "my-tasks", "everywhere");
        var (panel, created) = store.Show("s1", Json("""{ "id": "my-tasks", "title": "Tasks", "markdown": "# None", "scope": "session" }"""));
        Assert.False(created);
        Assert.Equal("everywhere", panel.Scope);
        Assert.Null(panel.Items);
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public void Update_changes_only_what_it_names()
    {
        var store = new PanelStore(StoreFile);
        store.Show("s1", Json(TasksPanel));
        var panel = store.Update("s1", Json("""{ "id": "my-tasks", "subtitle": "LlamaTrack · ACME" }"""));
        Assert.Equal("My tasks", panel.Title);
        Assert.Equal("LlamaTrack · ACME", panel.Subtitle);
        Assert.Single(panel.Items!);
    }

    [Fact]
    public void A_failed_update_leaves_the_panel_as_it_was()
    {
        var store = new PanelStore(StoreFile);
        store.Show("s1", Json(TasksPanel));
        Assert.Throws<PanelException>(() => store.Update("s1", Json("""{ "id": "my-tasks", "title": "New", "items": [ { "title": "x", "link": "javascript:alert(1)" } ] }""")));
        Assert.Equal("My tasks", store.Snapshot()[0].Title);
    }

    [Fact]
    public void Sessions_only_touch_their_own_panels()
    {
        var store = new PanelStore(StoreFile);
        store.Show("s1", Json(TasksPanel));
        Assert.Throws<PanelException>(() => store.Update("s2", Json("""{ "id": "my-tasks", "title": "Mine now" }""")));
        Assert.False(store.Close("s2", "my-tasks"));
        store.Show("s2", Json(TasksPanel));   // the same id in another session is another panel
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Theory]
    [InlineData("""{ "id": "x", "title": "T" }""", "content")]
    [InlineData("""{ "id": "x", "title": "T", "markdown": "a", "items": [] }""", "one of markdown, items or groups")]
    [InlineData("""{ "id": "bad id", "title": "T", "markdown": "a" }""", "id may only")]
    [InlineData("""{ "id": "x", "markdown": "a" }""", "title is required")]
    [InlineData("""{ "id": "x", "title": "T", "markdown": "a", "scope": "world" }""", "scope must be one of")]
    [InlineData("""{ "id": "x", "title": "T", "markdown": "a", "refresh": "one\ntwo" }""", "one line")]
    [InlineData("""{ "id": "x", "title": "T", "items": [ { "title": "a", "link": "file:///C:/x" } ] }""", "items[0]: link")]
    [InlineData("""{ "id": "x", "title": "T", "items": [ { "title": "a", "buttons": [ { "label": "Go" } ] } ] }""", "prompt is required")]
    public void Mistakes_are_explained(string args, string message)
    {
        var ex = Assert.Throws<PanelException>(() => new PanelStore(StoreFile).Show("s1", Json(args)));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Items_can_come_in_collapsible_groups()
    {
        var store = new PanelStore(StoreFile);
        var (panel, _) = store.Show("s1", Json("""
            { "id": "board", "title": "Board", "groups": [
                { "title": "In progress", "items": [ { "key": "LT-482", "title": "Tax rounding" } ] },
                { "title": "Backlog", "collapsed": true, "items": [] } ] }
            """));
        Assert.Null(panel.Items);
        Assert.Equal(["In progress", "Backlog"], panel.Groups!.Select(g => g.Title));
        Assert.True(panel.Groups[1].Collapsed);
        Assert.False(panel.Groups[0].Collapsed);
        Assert.Equal("LT-482", new PanelStore(StoreFile).Snapshot()[0].Groups![0].Items[0].Key);

        // Switching to Markdown drops the groups.
        Assert.Null(store.Update("s1", Json("""{ "id": "board", "markdown": "# Done" }""")).Groups);
    }

    [Theory]
    [InlineData("""{ "id": "x", "title": "T", "items": [], "groups": [] }""", "one of markdown, items or groups")]
    [InlineData("""{ "id": "x", "title": "T", "groups": [ { "items": [] } ] }""", "groups[0]: title is required")]
    [InlineData("""{ "id": "x", "title": "T", "groups": [ { "title": "A", "items": [ { "title": "a", "link": "ftp://x" } ] } ] }""", "groups[0]: items[0]: link")]
    public void Group_mistakes_are_explained(string args, string message)
    {
        var ex = Assert.Throws<PanelException>(() => new PanelStore(StoreFile).Show("s1", Json(args)));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Groups_can_hold_groups_three_levels_deep()
    {
        var (panel, _) = new PanelStore(StoreFile).Show("s1", Json("""
            { "id": "board", "title": "Board", "groups": [
                { "title": "To do", "items": [ { "title": "Unsorted" } ], "groups": [
                    { "title": "High", "groups": [ { "title": "Backend", "items": [ { "key": "LT-1", "title": "a" } ] } ] },
                    { "title": "Low", "collapsed": true, "items": [ { "key": "LT-2", "title": "b" } ] } ] } ] }
            """));
        var todo = Assert.Single(panel.Groups!);
        Assert.Single(todo.Items);
        Assert.Equal(["High", "Low"], todo.Groups!.Select(g => g.Title));
        Assert.Equal("LT-1", todo.Groups[0].Groups![0].Items[0].Key);
        Assert.Empty(todo.Groups[0].Items);   // a group may hold only groups
        Assert.Equal(4, panel.Groups!.SelectMany(PanelSpec.Flatten).Count());

        var tooDeep = """{ "id": "x", "title": "T", "groups": [ { "title": "1", "groups": [ { "title": "2", "groups": [ { "title": "3", "groups": [ { "title": "4" } ] } ] } ] } ] }""";
        Assert.Contains("groups[0]: groups[0]: groups[0]: Groups go at most 3 levels deep",
            Assert.Throws<PanelException>(() => new PanelStore(StoreFile).Show("s1", Json(tooDeep))).Message);
    }

    [Fact]
    public void Nested_groups_count_towards_the_limits()
    {
        var items = string.Join(",", Enumerable.Range(0, 120).Select(i => $$"""{ "title": "i{{i}}" }"""));
        var args = $$"""{ "id": "x", "title": "T", "groups": [ { "title": "A", "items": [{{items}}], "groups": [ { "title": "B", "items": [{{items}}] } ] } ] }""";
        Assert.Contains("across the groups", Assert.Throws<PanelException>(() => new PanelStore(StoreFile).Show("s1", Json(args))).Message);
    }

    [Fact]
    public void Groups_share_the_item_limit()
    {
        var items = string.Join(",", Enumerable.Range(0, 120).Select(i => $$"""{ "title": "i{{i}}" }"""));
        var args = $$"""{ "id": "x", "title": "T", "groups": [ { "title": "A", "items": [{{items}}] }, { "title": "B", "items": [{{items}}] } ] }""";
        Assert.Contains("across the groups", Assert.Throws<PanelException>(() => new PanelStore(StoreFile).Show("s1", Json(args))).Message);
    }

    private const string RichPanel = """
        { "id": "rich", "title": "Errors", "badge": { "text": "3", "tone": "danger" },
          "stats": [ { "label": "Errors today", "value": 214, "tone": "danger", "change": "up", "note": "+40", "series": [1, 4, 9] } ],
          "groups": [ { "title": "New", "items": [
            { "key": "E-1", "title": "NullReferenceException", "details": "```cs\nthrow null;\n```", "time": "2026-10-23T14:05:00+01:00",
              "checked": false, "progress": { "value": 3, "max": 8 }, "series": [0, 2, 5],
              "buttons": [ { "label": "Copy", "text": "git checkout fix/e-1" }, { "label": "Ask", "prompt": "Look at E-1" } ] },
            { "title": "Timeout", "checked": true } ] } ] }
        """;

    [Fact]
    public void Items_carry_details_time_ticks_progress_and_trends_and_the_panel_a_badge_and_stats()
    {
        var (panel, _) = new PanelStore(StoreFile).Show("s1", Json(RichPanel));
        Assert.Equal("3", panel.Badge!.Text);
        Assert.Equal("danger", panel.Badge.Tone);
        var stat = Assert.Single(panel.Stats!);
        Assert.Equal(("214", "up", 3), (stat.Value, stat.Change, stat.Series!.Count));

        var item = panel.Groups![0].Items[0];
        Assert.StartsWith("```cs", item.Details);
        Assert.Equal(new DateTime(2026, 10, 23, 13, 5, 0, DateTimeKind.Utc), item.Time);
        Assert.False(item.Checked);
        Assert.Equal((3d, 8d), (item.Progress!.Value, item.Progress.Max));
        Assert.Equal(["copy", "here"], item.Buttons!.Select(b => b.Target));
        Assert.Equal("git checkout fix/e-1", item.Buttons[0].Prompt);
        Assert.Null(panel.Groups[0].Items[1].Progress);
    }

    [Theory]
    [InlineData("""{ "title": "a", "time": "yesterday-ish" }""", "time must be")]
    [InlineData("""{ "title": "a", "checked": "yes" }""", "checked must be true or false")]
    [InlineData("""{ "title": "a", "progress": { "value": 9, "max": 8 } }""", "between 0 and max")]
    [InlineData("""{ "title": "a", "progress": { "value": 1, "max": 0 } }""", "more than 0")]
    [InlineData("""{ "title": "a", "series": [1] }""", "2 to 60")]
    [InlineData("""{ "title": "a", "series": [1, "x"] }""", "array of numbers")]
    [InlineData("""{ "title": "a", "buttons": [ { "label": "Copy", "target": "copy" } ] }""", "copy button needs text")]
    public void New_item_mistakes_are_explained(string item, string message)
    {
        var ex = Assert.Throws<PanelException>(() => new PanelStore(StoreFile).Show("s1", Json($$"""{ "id": "x", "title": "T", "items": [ {{item}} ] }""")));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void The_persons_ticks_survive_Claude_sending_the_items_again_and_Claude_can_set_them()
    {
        var store = new PanelStore(StoreFile);
        var (panel, _) = store.Show("s1", Json(RichPanel));
        var seen = panel.Version;
        Assert.True(store.SetChecked("s1", "rich", "0/0", true, seen));
        Assert.False(store.SetChecked("s1", "rich", "0/0", false, seen));   // made on an older copy: ignored

        var again = store.Show("s1", Json(RichPanel)).Panel;   // a refresh sends checked: false again
        Assert.True(again.Groups![0].Items[0].Checked);
        Assert.True(again.Groups[0].Items[1].Checked);           // matched by title

        var updated = store.Update("s1", Json("""{ "id": "rich", "checks": { "E-1": false, "Timeout": false } }"""));
        Assert.All(updated.Groups![0].Items, i => Assert.False(i.Checked));
        Assert.Throws<PanelException>(() => store.Update("s1", Json("""{ "id": "rich", "checks": { "nope": true } }""")));
    }

    [Theory]
    [InlineData("1")]        // no top-level items: this panel has groups
    [InlineData("0/5")]      // no such item
    [InlineData("0/x")]
    [InlineData("3/0")]      // no such group
    public void A_tick_on_something_that_isnt_a_tick_box_is_ignored(string path)
    {
        var store = new PanelStore(StoreFile);
        var (panel, _) = store.Show("s1", Json(RichPanel));
        Assert.False(store.SetChecked("s1", "rich", path, true, panel.Version));
    }

    [Fact]
    public void Get_panel_returns_the_panel_with_its_ticks()
    {
        var store = new PanelStore(StoreFile);
        var mcp = new PanelMcp(store);
        var (panel, _) = store.Show("s1", Json(RichPanel));
        store.SetChecked("s1", "rich", "0/0", true, panel.Version);
        var reply = Call(mcp, "s1", """{ "jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": { "name": "get_panel", "arguments": { "id": "rich" } } }""");
        var json = JsonNode.Parse((string)reply["result"]!["content"]![0]!["text"]!)!;
        Assert.True((bool)json["groups"]![0]!["items"]![0]!["checked"]!);
        Assert.Null(json["owner"]);
        Assert.Null(json["groups"]![0]!["items"]![1]!["progress"]);   // nulls left out

        var other = Call(mcp, "s2", """{ "jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": { "name": "get_panel", "arguments": { "id": "rich" } } }""");
        Assert.True((bool)other["result"]!["isError"]!);              // another session's panel isn't readable
    }

    [Fact]
    public void Badge_and_stats_are_cleared_with_null_and_kept_when_left_out()
    {
        var store = new PanelStore(StoreFile);
        store.Show("s1", Json(RichPanel));
        var kept = store.Update("s1", Json("""{ "id": "rich", "title": "Errors!" }"""));
        Assert.NotNull(kept.Badge);
        Assert.NotNull(kept.Stats);
        var cleared = store.Update("s1", Json("""{ "id": "rich", "badge": null, "stats": null }"""));
        Assert.Null(cleared.Badge);
        Assert.Null(cleared.Stats);
        Assert.Equal("7", store.Update("s1", Json("""{ "id": "rich", "badge": 7 }""")).Badge!.Text);
    }

    [Fact]
    public void A_closed_panel_can_be_reopened_as_it_was()
    {
        var store = new PanelStore(StoreFile);
        var (panel, _) = store.Show("s1", Json(RichPanel));
        store.SetChecked("s1", "rich", "0/0", true, panel.Version);
        store.Close("s1", "rich");
        Assert.Equal("rich", Assert.Single(store.Closed()).Id);

        Assert.True(store.Reopen("s1", "rich"));
        Assert.True(Assert.Single(store.Snapshot()).Groups![0].Items[0].Checked);   // ticks and all
        Assert.Empty(store.Closed());
        Assert.False(store.Reopen("s1", "rich"));                                    // only once
    }

    [Fact]
    public void A_closed_panel_whose_id_is_in_use_again_or_whose_session_is_gone_isnt_offered()
    {
        var store = new PanelStore(StoreFile);
        store.Show("s1", Json(TasksPanel));
        store.Close("s1", "my-tasks");
        store.Show("s1", Json(TasksPanel));          // Claude showed it again meanwhile
        Assert.Empty(store.Closed());
        Assert.False(store.Reopen("s1", "my-tasks"));

        store.Show("s2", Json(TasksPanel));
        store.Close("s2", "my-tasks");
        store.KeepOwners(["s1"]);                    // s2 was removed
        Assert.Empty(store.Closed());
    }

    [Fact]
    public void A_session_has_at_most_twenty_panels()
    {
        var store = new PanelStore(StoreFile);
        for (var i = 0; i < PanelSpec.MaxPanelsPerSession; i++) store.Show("s1", Json($$"""{ "id": "p{{i}}", "title": "T", "markdown": "a" }"""));
        Assert.Throws<PanelException>(() => store.Show("s1", Json("""{ "id": "one-more", "title": "T", "markdown": "a" }""")));
        store.Show("s1", Json("""{ "id": "p3", "title": "Replaced", "markdown": "b" }"""));   // replacing is fine
    }

    [Fact]
    public void A_removed_sessions_panels_go_with_it()
    {
        var store = new PanelStore(StoreFile);
        store.Show("s1", Json(TasksPanel));
        store.Show("s2", Json(TasksPanel));
        store.KeepOwners(["s2"]);
        Assert.Equal("s2", Assert.Single(new PanelStore(StoreFile).Snapshot()).Owner);
    }

    // ---- MCP

    private static JsonObject Call(PanelMcp mcp, string session, string json) => mcp.Handle(session, Json(json))!;

    [Fact]
    public void Initialize_agrees_a_protocol_version_and_names_the_server()
    {
        var mcp = new PanelMcp(new PanelStore(StoreFile));
        string? connected = null;
        mcp.Initialized += s => connected = s;
        var reply = Call(mcp, "s1", """{ "jsonrpc": "2.0", "id": 1, "method": "initialize", "params": { "protocolVersion": "2025-03-26", "capabilities": {} } }""");
        Assert.Equal("2025-03-26", (string?)reply["result"]!["protocolVersion"]);
        Assert.Equal("claude-terminals", (string?)reply["result"]!["serverInfo"]!["name"]);
        Assert.Equal("s1", connected);
        var unknown = Call(mcp, "s1", """{ "jsonrpc": "2.0", "id": 2, "method": "initialize", "params": { "protocolVersion": "1999-01-01" } }""");
        Assert.Equal("2025-06-18", (string?)unknown["result"]!["protocolVersion"]);
    }

    [Fact]
    public void Notifications_get_no_reply_and_unknown_methods_an_error()
    {
        var mcp = new PanelMcp(new PanelStore(StoreFile));
        Assert.Null(mcp.Handle("s1", Json("""{ "jsonrpc": "2.0", "method": "notifications/initialized" }""")));
        Assert.Equal(-32601, (int)Call(mcp, "s1", """{ "jsonrpc": "2.0", "id": "a", "method": "resources/list" }""")["error"]!["code"]!);
    }

    [Fact]
    public void The_tools_are_listed_with_their_schemas()
    {
        var reply = Call(new PanelMcp(new PanelStore(StoreFile)), "s1", """{ "jsonrpc": "2.0", "id": 1, "method": "tools/list" }""");
        var tools = reply["result"]!["tools"]!.AsArray();
        Assert.Equal(new[] { "show_panel", "update_panel", "get_panel", "close_panel", "list_panels" }, tools.Select(t => (string)t!["name"]!));
        Assert.All(tools, t => Assert.Equal("object", (string?)t!["inputSchema"]!["type"]));
    }

    [Fact]
    public void Tool_calls_change_the_panels_and_mistakes_come_back_as_tool_errors()
    {
        var store = new PanelStore(StoreFile);
        var mcp = new PanelMcp(store);
        var shown = Call(mcp, "s1", $$"""{ "jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": { "name": "show_panel", "arguments": {{TasksPanel}} } }""");
        Assert.False((bool)shown["result"]!["isError"]!);
        Assert.Contains("My tasks", (string?)shown["result"]!["content"]![0]!["text"]);

        var listed = Call(mcp, "s1", """{ "jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": { "name": "list_panels" } }""");
        Assert.Contains("my-tasks", (string?)listed["result"]!["content"]![0]!["text"]);

        var bad = Call(mcp, "s1", """{ "jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": { "name": "show_panel", "arguments": { "id": "x" } } }""");
        Assert.True((bool)bad["result"]!["isError"]!);

        Call(mcp, "s1", """{ "jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": { "name": "close_panel", "arguments": { "id": "my-tasks" } } }""");
        Assert.Empty(store.Snapshot());

        Assert.Equal(-32602, (int)Call(mcp, "s1", """{ "jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": { "name": "nope" } }""")["error"]!["code"]!);
    }

    // ---- HTTP

    [Fact]
    public async Task The_server_answers_only_a_sessions_token_and_never_a_web_page()
    {
        var store = new PanelStore(StoreFile);
        using var server = PanelServer.TryStart(new PanelMcp(store))!;
        Assert.NotNull(server);
        var token = server.TokenFor("s1");
        var config = JsonNode.Parse(server.ConfigJson("s1"))!["mcpServers"]!["claude-terminals"]!;
        Assert.Equal("http", (string?)config["type"]);
        Assert.Equal(server.Url, (string?)config["url"]);
        Assert.Equal("Bearer " + token, (string?)config["headers"]!["Authorization"]);

        using var http = new HttpClient();
        HttpRequestMessage Post(string body, string? bearer)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            r.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (bearer != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return r;
        }
        var call = $$"""{ "jsonrpc": "2.0", "id": 7, "method": "tools/call", "params": { "name": "show_panel", "arguments": {{TasksPanel}} } }""";

        var ok = await http.SendAsync(Post(call, token));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(7, (int)JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["id"]!);
        Assert.Equal("s1", Assert.Single(store.Snapshot()).Owner);

        Assert.Equal(HttpStatusCode.Accepted, (await http.SendAsync(Post("""{ "jsonrpc": "2.0", "method": "notifications/initialized" }""", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post(call, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post(call, "guess"))).StatusCode);

        var fromPage = Post(call, token);
        fromPage.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(fromPage)).StatusCode);

        var get = new HttpRequestMessage(HttpMethod.Get, server.Url);
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.SendAsync(get)).StatusCode);

        server.Forget("s1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post(call, token))).StatusCode);
    }
}
