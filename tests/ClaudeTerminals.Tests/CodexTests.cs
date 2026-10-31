using System.Text.Json;
using ClaudeTerminals.Sessions;

namespace ClaudeTerminals.Tests;

/// <summary>Codex sessions: their command lines, and finding their conversations in Codex's rollout files.</summary>
public class CodexCommandTests
{
    private const string Id = "01a1891c-aad9-7a73-8004-10a6cabcf58e";
    private const string Url = "http://127.0.0.1:5000/mcp";

    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("claude.exe --model opus", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("Codex.cmd -m o3", "codex")]
    [InlineData("codexx", "none")]
    [InlineData("pwsh", "none")]
    [InlineData("", "none")]
    public void The_agent_is_the_program(string command, string expected) => Assert.Equal(expected, Agents.For(command)?.Id ?? "none");

    [Fact]
    public void Every_agent_is_found_by_its_programs_and_gives_the_page_patterns_that_work()
    {
        foreach (var agent in Agents.All)
        {
            Assert.All(agent.Programs, p => Assert.Same(agent, Agents.For(p + " --some-option")));
            // The page runs these as JavaScript regexes; they keep to the syntax both share.
            foreach (var pattern in new[] { agent.OwnConversationPattern, agent.ContinuePattern }.OfType<string>())
                _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.ECMAScript | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        Assert.Equal(Agents.All.Count, Agents.All.Select(a => a.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("codex", true, "codex resume " + Id)]
    [InlineData("codex -m o3", true, "codex resume " + Id + " -m o3")]
    [InlineData("codex", false, "codex")]                        // not saved: a new conversation, learned once Codex writes it
    [InlineData("codex resume --last", true, "codex resume --last")]   // the command picks its own
    [InlineData("codex exec hello", true, "codex exec hello")]
    [InlineData("claude", true, "claude")]
    public void A_codex_session_resumes_its_conversation(string command, bool saved, string expected) =>
        Assert.Equal(expected, CodexAgent.PrepareCommand(command, Id, _ => saved));

    [Fact]
    public void A_new_conversation_starts_with_the_panels_prompt() =>
        Assert.Equal("codex \"fix it\" -m o3", CodexAgent.PrepareCommand("codex -m o3", Id, _ => true, newConversation: true, firstPrompt: "fix it"));

    [Fact]
    public void Panels_are_added_as_an_mcp_server_with_the_token_from_the_environment()
    {
        var line = CodexAgent.PrepareCommand("codex", null, _ => false, Url);
        Assert.Equal($"codex -c mcp_servers.claude-terminals.url={Url} -c mcp_servers.claude-terminals.bearer_token_env_var={CodexAgent.PanelTokenVariable}", line);
        Assert.DoesNotContain("Bearer", line);
    }

    [Fact]
    public void A_command_with_its_own_panel_server_settings_keeps_them() =>
        Assert.Equal("codex -c mcp_servers.claude-terminals.enabled=false",
            CodexAgent.PrepareCommand("codex -c mcp_servers.claude-terminals.enabled=false", null, _ => false, Url));

    [Theory]
    [InlineData("codex", true)]
    [InlineData("codex -m o3", true)]
    [InlineData("codex resume --last", false)]
    [InlineData("codex exec hi", false)]
    public void The_app_tracks_an_interactive_codex(string command, bool expected) =>
        Assert.Equal(expected, TerminalSession.CanTrackConversation(command));

    [Fact]
    public void Codex_resume_typed_as_the_command_becomes_the_sessions_conversation() =>
        Assert.Equal(("codex -m o3", Id), TerminalSession.TakeConversation($"codex resume {Id} -m o3"));

    [Fact]
    public void Codexs_dash_c_is_an_option_not_continue() =>
        Assert.Equal("codex -c model=o3", TerminalSession.StripContinue("codex -c model=o3"));

    [Fact]
    public void A_codex_session_is_never_given_an_id_codex_cant_use() =>
        Assert.False(TerminalSession.NeedsOwnConversation(new SessionInfo { Command = "codex", Directory = "C:\\x" }));

    // Lines as the agents draw them (Codex 0.166's folder trust check, Claude's permission prompt and prompt box).
    [Theory]
    [InlineData("codex", "› 1. Trust and continue", true)]
    [InlineData("codex", "› 1. Fix the failing test", false)]   // a prompt typed into its prompt box
    [InlineData("codex", "› Ask Codex to do anything", false)]
    [InlineData("claude", "❯ 1. Yes", true)]
    [InlineData("claude", " Esc to cancel · Tab to amend", true)]
    [InlineData("claude", "> fix the bug", false)]
    public void The_page_knows_an_agent_is_asking_from_its_menu(string command, string line, bool asking) =>
        Assert.Equal(asking, System.Text.RegularExpressions.Regex.IsMatch(line, Agents.For(command)!.Asks!.Choice));

    [Fact]
    public void The_page_is_told_how_each_agent_asks()
    {
        var json = JsonSerializer.SerializeToElement(Agents.Codex.Describe(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("Trust and continue", json.GetProperty("asks").GetProperty("choice").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("asks").GetProperty("promptRule").ValueKind);
    }

    [Fact]
    public void The_page_is_told_each_agents_colour_for_both_themes()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var agent in Agents.All)
        {
            var color = JsonSerializer.SerializeToElement(agent.Describe(), options).GetProperty("color");
            Assert.StartsWith("#", color.GetProperty("dark").GetString());
            Assert.StartsWith("#", color.GetProperty("light").GetString());
        }
    }
}

/// <summary>Sets CODEX_HOME for the whole process, so not run in parallel.</summary>
[Collection(nameof(GitUndoTests))]
public sealed class CodexConversationTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "ct-codex-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string work = Path.Combine(Path.GetTempPath(), "ct-codex-work-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");

    public CodexConversationTests()
    {
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(work);
        Environment.SetEnvironmentVariable("CODEX_HOME", home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
        foreach (var dir in new[] { home, work })
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Line(object o) => JsonSerializer.Serialize(o) + "\n";

    /// <summary>A rollout as Codex writes it: session_meta, its own context as a user message, then the prompt.</summary>
    private string Rollout(string id, DateTime startedUtc, string cwd, string prompt, object? source = null, object? meta = null)
    {
        var local = startedUtc.ToLocalTime();
        var dir = Path.Combine(home, "sessions", local.ToString("yyyy"), local.ToString("MM"), local.ToString("dd"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-{local:yyyy-MM-ddTHH-mm-ss}-{id}.jsonl");
        File.WriteAllText(path,
            Line(new { timestamp = startedUtc.ToString("O"), type = "session_meta", payload = meta ?? new { id, timestamp = startedUtc.ToString("O"), cwd, source = source ?? "cli" } }) +
            Line(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "<environment_context>…</environment_context>" } } } }) +
            Line(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = prompt } } } }));
        return path;
    }

    private static string NewId() => Guid.NewGuid().ToString();

    [Fact]
    public void Lists_a_folders_conversations_with_their_first_prompt_and_name()
    {
        var a = NewId();
        var b = NewId();
        Rollout(a, DateTime.UtcNow.AddMinutes(-5), work, "Add a login page");
        Rollout(b, DateTime.UtcNow.AddMinutes(-4), Path.GetTempPath(), "Elsewhere");
        Rollout(NewId(), DateTime.UtcNow.AddMinutes(-3), work, "scripted", source: "exec");
        File.WriteAllText(Path.Combine(home, "session_index.jsonl"), Line(new { id = a, thread_name = "Login page" }));

        var list = CodexConversations.List(work, 10);

        var c = Assert.Single(list);
        Assert.Equal((a, "Login page", "Add a login page", "codex"), (c.SessionId, c.Title, c.FirstPrompt, c.Agent));
        Assert.True(CodexConversations.IsSaved(a));
        Assert.Equal("Elsewhere", CodexConversations.Find(b)?.Title);
    }

    [Fact]
    public void A_session_learns_the_conversation_its_codex_started()
    {
        var started = DateTime.UtcNow.AddMinutes(-1);
        Rollout(NewId(), started.AddMinutes(-10), work, "before the session");
        Rollout(NewId(), started.AddSeconds(5), Path.GetTempPath(), "another folder");
        var mine = NewId();
        Rollout(mine, started.AddSeconds(10), work, "hello");

        Assert.Equal(mine, CodexConversations.Live(work, started, new HashSet<string>()));
        Assert.Null(CodexConversations.Live(work, started, new HashSet<string> { mine }));   // another session is in it

        var next = NewId();   // /new
        Rollout(next, started.AddSeconds(30), work, "again");
        Assert.Equal(next, CodexConversations.Live(work, started, new HashSet<string>()));
    }

    /// <summary>A prompt as Codex records it in history.jsonl.</summary>
    private void Prompted(string id, DateTime atUtc) =>
        File.AppendAllText(Path.Combine(home, "history.jsonl"), Line(new { session_id = id, ts = new DateTimeOffset(atUtc).ToUnixTimeSeconds(), text = "go on" }));

    [Fact]
    public void A_session_follows_codex_to_a_conversation_picked_with_resume()
    {
        var started = DateTime.UtcNow.AddMinutes(-2);
        var old = NewId();
        Rollout(old, started.AddDays(-3), work, "last week's work");
        var elsewhere = NewId();
        Rollout(elsewhere, started.AddDays(-3), Path.GetTempPath(), "another folder");
        var mine = NewId();
        Rollout(mine, started.AddSeconds(5), work, "hello");
        Prompted(old, started.AddMinutes(-30));   // before the session: not this session's doing
        Assert.Equal(mine, CodexConversations.Live(work, started, new HashSet<string>()));

        Prompted(old, started.AddSeconds(40));   // /resume, then a prompt
        Assert.Equal(old, CodexConversations.Live(work, started, new HashSet<string>()));
        Assert.Equal(mine, CodexConversations.Live(work, started, new HashSet<string> { old }));   // another session is in it

        Prompted(elsewhere, started.AddSeconds(50));   // a Codex in another folder
        Assert.Equal(old, CodexConversations.Live(work, started, new HashSet<string>()));

        var next = NewId();   // /new after that
        Rollout(next, started.AddSeconds(60), work, "fresh start");
        Assert.Equal(next, CodexConversations.Live(work, started, new HashSet<string>()));
    }

    [Fact]
    public void A_thread_codex_starts_under_a_conversation_is_never_one_to_resume()
    {
        // As Codex writes a Guardian review: it names its parent, and codex resume refuses it ("Resume its parent session instead").
        var started = DateTime.UtcNow.AddMinutes(-1);
        var parent = NewId();
        Rollout(parent, started.AddSeconds(5), work, "build the thing");
        var review = NewId();
        Rollout(review, started.AddSeconds(20), work, "review this command", meta: new
        {
            id = review, timestamp = started.AddSeconds(20).ToString("O"), cwd = work, parent_thread_id = parent,
            source = new { subagent = new { other = "guardian" } }, thread_source = "guardian_review",
        });

        Assert.Equal([parent], CodexConversations.List(work, 10).Select(c => c.SessionId));
        Assert.Equal(parent, CodexConversations.Live(work, started, new HashSet<string>()));
        Assert.False(CodexConversations.IsSaved(review));
        Assert.True(CodexConversations.IsSaved(parent));
        Assert.Equal(parent, CodexConversations.Resumable(review));   // a session that took it reopens its conversation
        Assert.Equal(parent, CodexConversations.Find(review)?.SessionId);
    }

    private static string Item(object payload, string time = "2026-10-29T19:22:03.000Z") =>
        Line(new { timestamp = time, type = "response_item", payload });

    private static object Message(string role, string text, string kind) =>
        new { type = "message", role, content = new[] { new { type = kind, text } } };

    [Fact]
    public void The_rail_reads_a_codex_conversation_as_prompts_and_answers()
    {
        // Shapes as Codex 0.154 writes them (see a rollout in ~/.codex/sessions).
        var id = NewId();
        var path = Rollout(id, DateTime.UtcNow.AddMinutes(-1), work, "Fix the login bug");
        File.AppendAllText(path,
            Item(new { type = "reasoning", summary = Array.Empty<object>() }) +
            Item(Message("assistant", "I'll look at the login code first.", "output_text")) +
            Item(new { type = "custom_tool_call", name = "exec", input = "text(await tools.exec_command({cmd:\"git status --short\",\"max_output_tokens\":2000}))" }) +
            Item(new { type = "custom_tool_call_output", output = "ok" }) +
            Item(new { type = "custom_tool_call", name = "apply_patch", input = "*** Begin Patch\n*** Update File: src/Login.cs\n@@\n-a\n+b\n*** End Patch" }) +
            Item(new { type = "custom_tool_call", name = "exec", input = "text(await tools.apply_patch(\"*** Begin Patch\\n*** Update File: C:\\\\src\\\\Auth.cs\\n@@\\n-a\\n+b\\n*** End Patch\"))" }) +
            Item(new { type = "function_call", name = "shell", arguments = "{\"command\":[\"dotnet\",\"test\"]}" }) +
            Item(Message("assistant", "Fixed: the token was never refreshed.", "output_text")) +
            Line(new { type = "event_msg", payload = new { type = "user_message", message = "Thanks" } }) +   // the same prompt again, as an event
            Item(Message("user", "<environment_context>…</environment_context>", "input_text")) +
            Item(Message("user", "<send_user_message_question_reply>[…]</send_user_message_question_reply>", "input_text")) +
            Item(Message("user", "Thanks", "input_text"), "2026-10-29T19:30:00.000Z"));

        var c = CodexConversations.Read(id);

        Assert.NotNull(c);
        Assert.Equal(id, c.SessionId);
        Assert.Equal(["Fix the login bug", "Thanks"], c.Turns.Select(t => t.Prompt));
        Assert.Equal(
            [("text", "I'll look at the login code first.", (string?)null), ("tool", "git status --short", "exec"),
             ("tool", "src/Login.cs", "apply_patch"), ("tool", @"C:\src\Auth.cs", "exec"), ("tool", "dotnet test", "shell"), ("text", "Fixed: the token was never refreshed.", null)],
            c.Turns[0].Items.Select(i => (i.Kind, i.Text, i.Tool)));
        Assert.Equal(new DateTime(2026, 10, 29, 19, 30, 0, DateTimeKind.Utc), c.Turns[1].Time?.ToUniversalTime());

        // Only what Codex adds since is read next time, and the version tells the page something changed.
        File.AppendAllText(path, Item(Message("assistant", "You're welcome.", "output_text")));
        var again = CodexConversations.Read(id);
        Assert.NotEqual(c.Version, again!.Version);
        Assert.Equal("You're welcome.", Assert.Single(again.Turns[1].Items).Text);
    }

    [Fact]
    public void A_codex_session_shows_its_own_conversation()
    {
        var id = NewId();
        Rollout(id, DateTime.UtcNow.AddMinutes(-1), work, "hello");
        using var s = new TerminalSession(new SessionInfo { Command = "codex", Directory = work, Conversation = id });

        Assert.True(s.Agent!.ShowsConversation);
        Assert.Equal("hello", Assert.Single(s.Agent.ReadConversation(s, [])!.Turns).Prompt);

        using var fresh = new TerminalSession(new SessionInfo { Command = "codex", Directory = work });   // nothing learned yet
        Assert.Null(fresh.Agent!.ReadConversation(fresh, []));
    }
}
