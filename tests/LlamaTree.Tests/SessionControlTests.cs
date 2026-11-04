using System.Text.Json;
using LlamaTree.Panels;
using LlamaTree.Relay;

namespace LlamaTree.Tests;

/// <summary>Sessions starting and driving other sessions, and what the person allows of it.</summary>
public class SessionControlTests
{
    private sealed class FakeHost : IControlHost
    {
        public readonly List<ControlSession> List = [];
        public readonly List<(string Id, string Text)> Typed = [];
        public readonly List<(string Id, string Keys)> Keys = [];
        public readonly List<string> Stopped = [];
        public string? Pending;

        public IReadOnlyList<ControlSession> Sessions() => [.. List];

        public Task<string> StartAsync(string parent, string name, string directory, string prompt)
        {
            var p = List.First(s => s.Id == parent);
            var id = "c" + (List.Count + 1);
            List.Add(new ControlSession(id, name, directory, p.Project, parent, true, true));
            return Task.FromResult(id);
        }

        public Task<string> AddWorktreeAsync(string parent, string branch) => Task.FromResult($@"D:\repo-{branch.Replace('/', '-')}");
        public void TypePrompt(string id, string text) => Typed.Add((id, text));
        public void SendKeys(string id, string keys) => Keys.Add((id, keys));
        public void Stop(string id) => Stopped.Add(id);
        public SessionReport? Report(string id) => new("do it", "done it", Pending);
    }

    private readonly FakeHost host = new();
    private readonly ControlPolicy policy = new(null);
    private bool paused;
    private readonly SessionControl control;

    public SessionControlTests()
    {
        host.List.Add(new ControlSession("a", "Lead", @"D:\repo", "p1", null, true, true));
        host.List.Add(new ControlSession("b", "Other", @"D:\repo-other", "p1", null, true, true));
        host.List.Add(new ControlSession("x", "Elsewhere", @"D:\elsewhere", "p2", null, true, true));
        control = new SessionControl(policy, host, () => paused);
    }

    private async Task<string> StartAllowed(string from = "a", string? dir = null)
    {
        policy.Set(ControlAbility.Spawn, from, RelayDecision.Allow);
        return (await control.StartAsync(from, "Helper", "Fix the tests", dir, null)).Id;
    }

    [Fact]
    public async Task An_allowed_session_starts_one_in_its_folder_and_drives_it()
    {
        var id = await StartAllowed();

        var child = host.List.Single(s => s.Id == id);
        Assert.Equal(("a", @"D:\repo", "p1"), (child.StartedBy, child.Directory, child.Project));
        control.SetStatus(id, SessionControl.Working);   // it has worked on its first prompt, and is done
        control.SetStatus(id, SessionControl.Done);
        control.Prompt("a", "Helper", "Now the docs");
        Assert.Equal((id, "Now the docs"), host.Typed.Single());
        Assert.Contains(control.Log(), e => e.Action == "start" && e.Ok);
    }

    [Fact]
    public async Task Not_allowed_refuses_and_starts_nothing()
    {
        policy.Set(ControlAbility.Spawn, ControlPolicy.ProjectPrefix + "p1", RelayDecision.Deny);

        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "Helper", "go", null, null));

        Assert.Equal(3, host.List.Count);
        Assert.Contains(control.Log(), e => !e.Ok);
    }

    [Fact]
    public async Task With_no_rule_the_person_is_asked_and_their_answer_can_become_the_rule()
    {
        SpawnRequest? asked = null;
        control.Asked += r => { asked = r; control.AnswerSpawn(r.Id, true, "session"); };

        var (id, _) = await control.StartAsync("a", "Helper", "go", null, null);

        Assert.Equal("Helper", asked!.Name);
        Assert.NotNull(host.List.SingleOrDefault(s => s.Id == id));
        Assert.Equal(RelayDecision.Allow, policy.Decide(ControlAbility.Spawn, "a", "p1").Decision);
        Assert.Empty(control.Pending());
    }

    [Fact]
    public async Task A_no_from_the_person_starts_nothing()
    {
        control.Asked += r => control.AnswerSpawn(r.Id, false, "once");

        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "Helper", "go", null, null));

        Assert.Equal(3, host.List.Count);
        Assert.Equal(RelayDecision.Ask, policy.Decide(ControlAbility.Spawn, "a", "p1").Decision);   // once: no rule made
    }

    [Fact]
    public async Task Folders_are_its_own_or_its_projects_or_inside_them_only()
    {
        policy.Set(ControlAbility.Spawn, "a", RelayDecision.Allow);

        var (_, dir) = await control.StartAsync("a", "Helper", "go", @"D:\repo-other\", null);   // another session of p1
        Assert.Equal(@"D:\repo-other", dir);
        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "Helper", "go", @"D:\elsewhere", null));   // p2's
        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "Helper", "go", @"C:\Windows", null));
        // Not a folder whose name only starts the same, and nothing climbing out.
        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "Helper", "go", @"D:\repository", null));
        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "Helper", "go", @"D:\repo\..\elsewhere", null));
        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "Helper", "go", @"..\elsewhere", null));
        var (_, wt) = await control.StartAsync("a", "Helper", "go", null, "feature/x");
        Assert.Equal(@"D:\repo-feature-x", wt);
    }

    // A folder inside its own or a project session's: given in full (either slash), or from its own folder.
    [Theory]
    [InlineData(@"D:\repo\src\api\", @"D:\repo\src\api")]
    [InlineData("D:/repo-other/docs", @"D:\repo-other\docs")]
    [InlineData("web", @"D:\repo\web")]
    [InlineData(@".\web\.", @"D:\repo\web")]
    public async Task A_folder_inside_its_own_or_its_projects_will_do(string given, string expected)
    {
        policy.Set(ControlAbility.Spawn, "a", RelayDecision.Allow);
        Assert.Equal(expected, (await control.StartAsync("a", "Helper", "go", given, null)).Directory);
    }

    // A session on a Linux server, seen from this computer: its paths stay its server's, whatever this computer is.
    [Theory]
    [InlineData("/home/dev/work/docs/", "/home/dev/work/docs")]
    [InlineData("src/api", "/home/dev/work/src/api")]
    [InlineData("/home/dev", null)]
    [InlineData("/home/dev/work/../other", null)]
    public async Task A_server_sessions_folders_are_its_servers_paths(string given, string? expected)
    {
        host.List.Add(new ControlSession("s", "Server lead", "/home/dev/work", "p3", null, true, true));
        policy.Set(ControlAbility.Spawn, "s", RelayDecision.Allow);
        if (expected == null) await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("s", "Helper", "go", given, null));
        else Assert.Equal(expected, (await control.StartAsync("s", "Helper", "go", given, null)).Directory);
    }

    [Fact]
    public async Task A_started_session_goes_only_by_its_own_rule_for_starting_more()
    {
        policy.Set(ControlAbility.Spawn, ControlPolicy.Everyone, RelayDecision.Allow);
        var child = (await control.StartAsync("a", "Helper", "go", null, null)).Id;

        Assert.Equal(RelayDecision.Allow, policy.Decide(ControlAbility.Spawn, "b", "p1").Decision);
        Assert.Equal(RelayDecision.Ask, policy.Decide(ControlAbility.Spawn, child, "p1", startedByAnother: true).Decision);
        var asked = false;
        control.Asked += r => { asked = true; control.AnswerSpawn(r.Id, false, "once"); };
        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync(child, "Grandchild", "go", null, null));
        Assert.True(asked);
    }

    [Fact]
    public async Task Only_its_own_sessions_can_be_driven()
    {
        await StartAllowed();
        control.SetStatus("b", SessionControl.Done);

        Assert.Throws<ControlException>(() => control.Prompt("a", "Other", "hi"));
        Assert.Throws<ControlException>(() => control.Stop("a", "b"));
        Assert.Throws<ControlException>(() => control.Prompt("b", "Helper", "hi"));   // a's, not b's
        Assert.Empty(host.Typed);
    }

    [Fact]
    public async Task Prompts_wait_for_an_idle_session()
    {
        var id = await StartAllowed();

        control.SetStatus(id, SessionControl.Working);
        Assert.Throws<ControlException>(() => control.Prompt("a", id, "more"));
        control.SetStatus(id, SessionControl.Asking);
        Assert.Throws<ControlException>(() => control.Prompt("a", id, "more"));
        Assert.Empty(host.Typed);
    }

    [Fact]
    public async Task Answering_prompts_is_allowed_by_default_and_can_be_turned_off_per_project()
    {
        var id = await StartAllowed();
        host.Pending = "Edit: src/calc.js";
        control.SetStatus(id, SessionControl.Asking);

        control.Answer("a", id, approve: true);
        Assert.Equal((id, "\r"), host.Keys.Single());

        policy.Set(ControlAbility.Answer, ControlPolicy.ProjectPrefix + "p1", RelayDecision.Deny);
        control.SetStatus(id, SessionControl.Done);
        control.SetStatus(id, SessionControl.Asking);
        Assert.Throws<ControlException>(() => control.Answer("a", id, approve: true));
        Assert.Single(host.Keys);
    }

    [Fact]
    public async Task Answering_needs_a_prompt_to_answer()
    {
        var id = await StartAllowed();
        control.SetStatus(id, SessionControl.Done);

        Assert.Throws<ControlException>(() => control.Answer("a", id, approve: true));
        Assert.Empty(host.Keys);
    }

    [Fact]
    public async Task Waiting_returns_once_it_has_worked_and_stopped()
    {
        var id = await StartAllowed();
        control.SetStatus(id, SessionControl.Working);
        control.SetStatus(id, SessionControl.Done);
        control.Prompt("a", id, "go on");

        var wait = control.WaitAsync("a", id, TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);   // idle just after the prompt: not done yet
        control.SetStatus(id, SessionControl.Working);
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);
        control.SetStatus(id, SessionControl.Done);

        var text = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("idle", text);
        Assert.Contains("done it", text);
    }

    [Fact]
    public async Task Waiting_says_what_a_waiting_session_asks()
    {
        var id = await StartAllowed();
        host.Pending = "Bash: npm test";
        control.SetStatus(id, SessionControl.Asking);

        var text = await control.WaitAsync("a", id, TimeSpan.FromSeconds(5));

        Assert.Contains("Bash: npm test", text);
        Assert.Contains("answer_prompt", text);
    }

    [Fact]
    public async Task There_are_limits_on_how_many_run()
    {
        policy.Set(ControlAbility.Spawn, ControlPolicy.Everyone, RelayDecision.Allow);
        for (var i = 0; i < SessionControl.MaxChildren; i++)
            host.List.Add(new ControlSession("k" + i, "Kid " + i, @"D:\repo", "p1", "a", true, true));

        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "One more", "go", null, null));

        host.List.RemoveAll(s => s.Id == "k0");
        host.List.Add(new ControlSession("k0", "Kid 0", @"D:\repo", "p1", "a", true, Running: false));   // a stopped one doesn't count
        await control.StartAsync("a", "One more", "go", null, null);
    }

    [Fact]
    public async Task Pausing_agents_stops_everything()
    {
        var id = await StartAllowed();
        control.SetStatus(id, SessionControl.Done);
        paused = true;

        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "Helper 2", "go", null, null));
        Assert.Throws<ControlException>(() => control.Prompt("a", id, "hi"));
        Assert.Throws<ControlException>(() => control.Stop("a", id));
        Assert.Empty(host.Typed);
        Assert.Empty(host.Stopped);
    }

    [Theory]
    [InlineData("fine\u001b[201~\u001b[Z")]   // out of the paste, then Shift+Tab (the child's permission mode)
    [InlineData("yes\u0003")]                 // Ctrl+C
    [InlineData("/permissions")]              // the agent's own commands
    [InlineData("!rm -rf src")]               // a shell command, run without asking
    public async Task Prompts_are_plain_text_only(string prompt)
    {
        var id = await StartAllowed();
        control.SetStatus(id, SessionControl.Working);
        control.SetStatus(id, SessionControl.Done);

        Assert.Throws<ControlException>(() => control.Prompt("a", id, prompt));
        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "Another", prompt, null, null));
        Assert.Empty(host.Typed);
        Assert.Single(host.List, s => s.StartedBy == "a");
    }

    [Fact]
    public async Task A_prompt_waits_until_the_last_one_has_been_taken_up()
    {
        var id = await StartAllowed();
        control.SetStatus(id, SessionControl.Done);   // idle, but hasn't started on its first prompt yet

        Assert.Throws<ControlException>(() => control.Prompt("a", id, "and another thing"));
        Assert.Empty(host.Typed);
    }

    [Theory]
    [InlineData(null)]                                   // not a tool's prompt: trusting the folder, say
    [InlineData("AskUserQuestion: Which database?")]     // a question with choices
    public async Task Only_tool_permission_prompts_can_be_answered(string? pending)
    {
        var id = await StartAllowed();
        host.Pending = pending;
        control.SetStatus(id, SessionControl.Asking);

        Assert.Throws<ControlException>(() => control.Answer("a", id, approve: true));
        Assert.Empty(host.Keys);
    }

    [Fact]
    public async Task One_question_at_a_time_per_session()
    {
        var asked = new TaskCompletionSource<SpawnRequest>();
        control.Asked += r => asked.TrySetResult(r);
        var first = control.StartAsync("a", "One", "go", null, null);
        var r = await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<ControlException>(() => control.StartAsync("a", "Two", "go", null, null));

        control.AnswerSpawn(r.Id, false, "once");
        await Assert.ThrowsAsync<ControlException>(() => first);
        Assert.Equal(3, host.List.Count);
    }

    [Fact]
    public void Rules_go_session_then_project_then_everyone()
    {
        Assert.Equal(RelayDecision.Ask, policy.Decide(ControlAbility.Spawn, "a", "p1").Decision);
        Assert.Equal(RelayDecision.Allow, policy.Decide(ControlAbility.Answer, "a", "p1").Decision);
        policy.Set(ControlAbility.Spawn, ControlPolicy.Everyone, RelayDecision.Deny);
        policy.Set(ControlAbility.Spawn, ControlPolicy.ProjectPrefix + "p1", RelayDecision.Allow);
        Assert.Equal(RelayDecision.Allow, policy.Decide(ControlAbility.Spawn, "a", "p1").Decision);
        Assert.Equal(RelayDecision.Deny, policy.Decide(ControlAbility.Spawn, "x", "p2").Decision);
        policy.Set(ControlAbility.Spawn, "a", RelayDecision.Deny);
        Assert.Equal(RelayDecision.Deny, policy.Decide(ControlAbility.Spawn, "a", "p1").Decision);

        policy.Keep(["b"], ["p1"]);   // a gone
        Assert.Equal(RelayDecision.Allow, policy.Decide(ControlAbility.Spawn, "a", "p1").Decision);
    }

    [Fact]
    public async Task The_tools_are_offered_and_answer_over_mcp()
    {
        var mcp = new PanelMcp(new PanelStore(Path.Combine(Path.GetTempPath(), $"panels-{Guid.NewGuid():N}.json")), new RelayHub(new RelayPolicy(null)), control);
        static JsonElement Msg(string json) => JsonDocument.Parse(json).RootElement;

        var list = (await mcp.HandleAsync("a", Msg("""{ "jsonrpc": "2.0", "id": 1, "method": "tools/list" }""")))!.ToJsonString();
        foreach (var tool in new[] { "start_session", "send_prompt", "get_session", "wait_for_session", "answer_prompt", "interrupt_session", "stop_session" })
            Assert.Contains($"\"{tool}\"", list);

        policy.Set(ControlAbility.Spawn, "a", RelayDecision.Allow);
        var reply = (await mcp.HandleAsync("a", Msg("""{ "jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": { "name": "start_session", "arguments": { "name": "Helper", "prompt": "go" } } }""")))!;
        Assert.False(reply["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Started session", reply.ToJsonString());

        var refused = (await mcp.HandleAsync("b", Msg("""{ "jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": { "name": "stop_session", "arguments": { "session": "Helper" } } }""")))!;
        Assert.True(refused["result"]!["isError"]!.GetValue<bool>());
    }
}
