using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeTerminals.Sessions;

namespace ClaudeTerminals.Tests;

/// <summary>
/// The conversation rail and Conversation tab: reading a transcript into prompts and answers, and finding the
/// transcript of the Claude running in a session. Sets CLAUDE_CONFIG_DIR for the whole process, so not run in parallel.
/// </summary>
[Collection(nameof(GitUndoTests))]
public sealed class ConversationReaderTests : IDisposable
{
    private readonly string config = Path.Combine(Path.GetTempPath(), "ct-conv-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? previousConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

    public ConversationReaderTests()
    {
        Directory.CreateDirectory(config);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", config);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousConfig);
        try { Directory.Delete(config, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Line(object o) => JsonSerializer.Serialize(o) + "\n";

    private static string User(object content, bool meta = false, bool sidechain = false, bool compact = false) => Line(new
    {
        type = "user", isMeta = meta, isSidechain = sidechain, isCompactSummary = compact,
        timestamp = "2026-10-19T10:00:00.000Z", message = new { role = "user", content },
    });

    private static string Assistant(params object[] content) => Line(new { type = "assistant", message = new { role = "assistant", content } });

    private static object Text(string t) => new { type = "text", text = t };
    private static object Tool(string name, object input) => new { type = "tool_use", id = "t1", name, input };

    private string Transcript(string name, string content)
    {
        var dir = Path.Combine(config, "projects", "D--work");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".jsonl");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void Prompts_start_turns_and_answers_collect_text_and_tool_calls()
    {
        var path = Transcript("s1",
            Assistant(Text("before any prompt: ignored")) +
            User("Fix the build") +
            Assistant(new { type = "thinking", thinking = "hidden" }, Text("Looking at it."), Tool("Bash", new { command = "dotnet build", description = "Build" })) +
            User(new object[] { new { type = "tool_result", tool_use_id = "t1", content = "ok" } }) +
            Assistant(Text("Fixed.")) +
            User("second prompt"));

        var c = ConversationReader.Read("s1", path);

        Assert.Equal(2, c.Turns.Count);
        Assert.Equal("Fix the build", c.Turns[0].Prompt);
        Assert.Equal(new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc), c.Turns[0].Time?.ToUniversalTime());
        Assert.Equal(
            ["text:Looking at it.", "tool:Bash:dotnet build", "text:Fixed."],
            c.Turns[0].Items.Select(i => i.Kind == "tool" ? $"tool:{i.Tool}:{i.Text}" : $"text:{i.Text}"));
        Assert.Equal("second prompt", c.Turns[1].Prompt);
        Assert.Empty(c.Turns[1].Items);
    }

    [Fact]
    public void Meta_sidechain_compact_interrupt_and_notification_entries_are_not_prompts()
    {
        var path = Transcript("s2",
            User("Caveat: the messages below…", meta: true) +
            User("subagent task", sidechain: true) +
            User("This session is being continued…", compact: true) +
            User(new object[] { Text("[Request interrupted by user]") }) +
            User("<task-notification>done</task-notification>") +
            User("<local-command-stdout>output</local-command-stdout>") +
            User("real prompt") +
            Line(new { type = "assistant", isSidechain = true, message = new { content = new[] { Text("subagent answer") } } }));

        var c = ConversationReader.Read("s2", path);

        Assert.Equal("real prompt", Assert.Single(c.Turns).Prompt);
        Assert.Empty(c.Turns[0].Items);
    }

    [Fact]
    public void Slash_commands_bang_commands_and_pasted_images_read_as_typed()
    {
        var path = Transcript("s3",
            User("<command-message>review</command-message>\n<command-name>/review</command-name>\n<command-args>123</command-args>") +
            User("<bash-input>git status</bash-input>") +
            User(new object[] { Text("What is this? [Image #1]"), Text("[Image: source: C:\\shot.png]"), new { type = "image" } }));

        var c = ConversationReader.Read("s3", path);

        Assert.Equal(["/review 123", "! git status", "What is this? [Image #1]"], c.Turns.Select(t => t.Prompt));
    }

    [Fact]
    public void Only_new_lines_are_read_and_a_half_written_line_waits_for_its_end()
    {
        var first = User("one") + Assistant(Text("answer one"));
        var path = Transcript("s4", first);
        var a = ConversationReader.Read("s4", path);
        Assert.Single(a.Turns);

        // Claude is mid-way through writing the next line.
        var next = User("two");
        File.AppendAllText(path, next[..10]);
        var b = ConversationReader.Read("s4", path);
        Assert.Single(b.Turns);
        Assert.Equal(a.Version, b.Version);

        File.AppendAllText(path, next[10..] + Assistant(Text("answer two")));
        var c = ConversationReader.Read("s4", path);
        Assert.Equal(["one", "two"], c.Turns.Select(t => t.Prompt));
        Assert.Equal("answer two", Assert.Single(c.Turns[1].Items).Text);
        Assert.NotEqual(b.Version, c.Version);

        // The earlier snapshot isn't changed by later reads.
        Assert.Single(a.Turns);
        Assert.Single(a.Turns[0].Items);
    }

    [Fact]
    public void A_rewritten_file_is_read_again_from_the_start()
    {
        var path = Transcript("s5", User("one") + User("two") + User("three"));
        Assert.Equal(3, ConversationReader.Read("s5", path).Turns.Count);

        File.WriteAllText(path, User("fresh"));
        Assert.Equal("fresh", Assert.Single(ConversationReader.Read("s5", path).Turns).Prompt);
    }

    [Fact]
    public void The_session_file_of_a_process_in_the_terminal_leads_to_its_transcript()
    {
        var work = Path.Combine(config, "work");
        Directory.CreateDirectory(work);
        var transcriptDir = Path.Combine(config, "projects", ConversationIndex.Encode(work));
        Directory.CreateDirectory(transcriptDir);
        File.WriteAllText(Path.Combine(transcriptDir, "abc.jsonl"), User("hello from the session"));

        using var session = new TerminalSession(new SessionInfo { Name = "t", Directory = work, Command = "cmd /c ping -n 30 127.0.0.1 >nul" });
        session.Start(80, 24);
        var pids = session.ProcessIds();
        Assert.NotEmpty(pids);

        // Stands in for the file Claude Code writes for its own process.
        var pid = pids[0];
        using (var p = Process.GetProcessById(pid))
        {
            var sessions = Path.Combine(config, "sessions");
            Directory.CreateDirectory(sessions);
            File.WriteAllText(Path.Combine(sessions, $"{pid}.json"), JsonSerializer.Serialize(new
            {
                pid, sessionId = "abc", cwd = work, kind = "interactive", procStart = p.StartTime.ToUniversalTime().ToFileTimeUtc(),
            }));
        }

        var c = ConversationReader.ForProcesses(session.ProcessIds());
        Assert.NotNull(c);
        Assert.Equal("abc", c.SessionId);
        Assert.Equal("hello from the session", Assert.Single(c.Turns).Prompt);
    }

    [Fact]
    public void A_session_file_left_by_an_earlier_process_with_the_same_id_is_ignored()
    {
        var pid = Environment.ProcessId;
        var sessions = Path.Combine(config, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, $"{pid}.json"), JsonSerializer.Serialize(new
        {
            pid, sessionId = "stale", kind = "interactive", procStart = DateTime.UtcNow.AddDays(-3).ToFileTimeUtc(),
        }));

        Assert.Null(ConversationReader.FindLiveSession([pid]));
    }
}
