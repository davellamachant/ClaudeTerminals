using System.Text;
using System.Text.Json;
using LlamaTree.Git;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>What plugins allowed to see sessions are told: a conversation read into events, with outcomes filled in from later results, and a repository's top-level folders.</summary>
public sealed class ActivityTests
{
    private static string Line(object o) => JsonSerializer.Serialize(o) + "\n";
    private const string Time = "2026-10-28T09:00:00.000Z";

    private static string User(object content, bool meta = false, bool sidechain = false) => Line(new
    {
        type = "user", isMeta = meta, isSidechain = sidechain, timestamp = Time, message = new { role = "user", content },
    });

    private static string Assistant(params object[] content) => Line(new { type = "assistant", timestamp = Time, message = new { role = "assistant", content } });
    private static object Text(string t) => new { type = "text", text = t };
    private static object Tool(string id, string name, object input) => new { type = "tool_use", id, name, input };
    private static object Result(string id, string content, bool error = false) => new { type = "tool_result", tool_use_id = id, content, is_error = error };

    private static List<ActivityEvent> Parse(params string[] lines)
    {
        var c = new ActivityReader.Cached();
        foreach (var l in lines) ActivityReader.AddLine(Encoding.UTF8.GetBytes(l.TrimEnd('\n')), c);
        return c.Events;
    }

    [Fact]
    public void Prompts_reads_edits_and_commands_become_events_with_their_files()
    {
        var ev = Parse(
            User("Fix the roster"),
            Assistant(Text("Looking."), Tool("r1", "Read", new { file_path = @"C:\repo\Roster.cs" })),
            Assistant(Tool("g1", "Grep", new { pattern = "Shift" })),
            Assistant(Tool("e1", "Edit", new { file_path = @"C:\repo\Roster.cs", old_string = "a", new_string = "b" })),
            Assistant(Tool("b1", "Bash", new { command = "dotnet build", description = "Build the solution" })),
            Assistant(Tool("t1", "PowerShell", new { command = "dotnet test tests" })),
            Assistant(Tool("x1", "Bash", new { command = "git status" })));
        Assert.Equal(["prompt", "read", "search", "edit", "build", "test", "run"], ev.Select(e => e.K));
        Assert.Equal("Fix the roster", ev[0].Text);
        Assert.Equal(@"C:\repo\Roster.cs", ev[1].File);
        Assert.Equal("Shift", ev[2].Text);
        Assert.Equal("Build the solution", ev[4].Text);   // the description when there is one
        Assert.Equal("git status", ev[6].Text);
        // Text beside a tool call is narration, not an answer.
        Assert.DoesNotContain(ev, e => e.K == "say");
    }

    [Fact]
    public void Outcomes_are_filled_in_when_the_results_arrive()
    {
        var ev = Parse(
            User("go"),
            Assistant(Tool("t1", "Bash", new { command = "dotnet test" })),
            User(new[] { Result("t1", "Failed!  - Tests.dll\n 2 failed, 10 passed") }),
            Assistant(Tool("e1", "Edit", new { file_path = @"C:\repo\A.cs" })),
            User(new[] { Result("e1", "<tool_use_error>String to replace not found in file.</tool_use_error>", error: true) }),
            Assistant(Tool("t2", "Bash", new { command = "dotnet test" })),
            User(new[] { Result("t2", "Passed!  - Tests.dll") }),
            Assistant(Tool("b1", "Bash", new { command = "dotnet build" })),
            User(new[] { Result("b1", "Build succeeded.") }),
            Assistant(Tool("x1", "Bash", new { command = "dir" })),
            User(new[] { Result("x1", "some listing") }));
        Assert.False(ev.Single(e => e.K == "test" && ev.IndexOf(e) == 1).Ok);
        Assert.False(ev.Single(e => e.K == "edit").Ok);
        Assert.True(ev.Where(e => e.K == "test").Last().Ok);
        Assert.True(ev.Single(e => e.K == "build").Ok);
        Assert.Null(ev.Single(e => e.K == "run").Ok);   // nothing to say about a plain command
        // Result lines are not prompts.
        Assert.Single(ev, e => e.K == "prompt");
    }

    [Fact]
    public void Questions_and_answers_are_told_apart()
    {
        var ev = Parse(
            User("go"),
            Assistant(Tool("q1", "AskUserQuestion", new { questions = new[] { new { question = "Which host should this use?", header = "Host" } } })),
            Assistant(Text("**Done.** The roster now balances nights. Anything else?")),
            Assistant(Text("# Result\n\nThe login did not reach this host, so nothing was changed. Second sentence here.")));
        Assert.Equal("ask", ev[1].K);
        Assert.Equal("Which host should this use?", ev[1].Text);
        Assert.True(ev[1].Q);
        Assert.Equal("say", ev[2].K);
        Assert.True(ev[2].Q);
        Assert.Equal("Done. The roster now balances nights.", ev[2].Text);
        Assert.False(ev[3].Q);
        Assert.Equal("Result The login did not reach this host, so nothing was changed.", ev[3].Text);
    }

    private static string Reply(string id, int ctxIn, int cached, int output, params object[] content) => Line(new
    {
        type = "assistant", timestamp = Time,
        message = new { id, role = "assistant", content, usage = new { input_tokens = ctxIn, cache_creation_input_tokens = 100, cache_read_input_tokens = cached, output_tokens = output } },
    });

    [Fact]
    public void A_reply_costs_once_however_many_lines_it_is_written_as()
    {
        var ev = Parse(
            User("go"),
            Reply("msg_1", 2, 1000, 50, Tool("r1", "Read", new { file_path = @"C:\repo\A.cs" })),
            Reply("msg_1", 2, 1000, 480, Tool("e1", "Edit", new { file_path = @"C:\repo\A.cs" })),   // the same reply, its usage so far
            Reply("msg_2", 2, 1600, 30, Text("Done.")),
            Assistant(Text("No usage on this one.")));
        Assert.Equal(["prompt", "read", "edit", "say", "say"], ev.Select(e => e.K));
        Assert.Equal((1102, 480), (ev[1].Ctx, ev[1].Out));   // input + cache creation + cache read; the largest output seen
        Assert.Null(ev[2].Ctx);
        Assert.Null(ev[2].Out);
        Assert.Equal((1702, 30), (ev[3].Ctx, ev[3].Out));
        Assert.Null(ev[4].Ctx);
    }

    [Fact]
    public void A_reply_whose_first_line_made_no_event_costs_its_first_event_later()
    {
        var ev = Parse(
            User("go"),
            Reply("msg_1", 2, 500, 10, new { type = "thinking", thinking = "hmm" }),
            Reply("msg_1", 2, 500, 90, Text("Thought about it.")));
        Assert.Equal(["prompt", "say"], ev.Select(e => e.K));
        Assert.Equal((602, 90), (ev[1].Ctx, ev[1].Out));
    }

    [Fact]
    public void Compactions_count_and_side_chains_meta_and_notifications_do_not()
    {
        var ev = Parse(
            User("go"),
            Line(new { type = "system", subtype = "compact_boundary", timestamp = Time }),
            User("a helper's prompt", sidechain: true),
            User("<local-command-stdout>noise</local-command-stdout>"),
            User("meta", meta: true),
            Line(new { type = "assistant", message = new { role = "assistant", content = new[] { Text("no timestamp") } } }));
        Assert.Equal(["prompt", "compact"], ev.Select(e => e.K));
    }

    [Fact]
    public void Reading_a_growing_file_appends_only_the_new_lines_and_keeps_pending_results()
    {
        var path = Path.Combine(Path.GetTempPath(), "ct-activity-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");
        try
        {
            File.WriteAllText(path, User("go") + Assistant(Tool("t1", "Bash", new { command = "dotnet test" })));
            var first = ActivityReader.Read(path);
            Assert.Equal(2, first.Version);
            Assert.Null(first.Events[1].Ok);
            File.AppendAllText(path, User(new[] { Result("t1", "Passed!") }) + Assistant(Text("All green.")));
            var second = ActivityReader.Read(path);
            Assert.Equal(3, second.Version);
            Assert.True(second.Events[1].Ok);   // the result arrived in the second read
            Assert.Equal("say", second.Events[2].K);
            // The first read's copy is unchanged.
            Assert.Null(first.Events[1].Ok);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fields_of_unexpected_types_are_skipped_and_reading_again_adds_nothing_twice()
    {
        var path = Path.Combine(Path.GetTempPath(), "ct-activity-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");
        try
        {
            File.WriteAllText(path,
                User("go") +
                Assistant(Tool("t1", "Bash", new { command = "dotnet test" })) +
                $$"""{"type":"assistant","timestamp":"{{Time}}","message":"a string where an object goes"}""" + "\n" +
                """{"type":"user","timestamp":12345,"message":{"content":"a number where a time goes"}}""" + "\n" +
                Line(new { type = "assistant", timestamp = Time, message = new { content = new object?[] { 5, null, new { type = "tool_use", name = "Read", input = "not an object" } }, usage = new[] { 1 } } }) +
                Line(new { type = "user", timestamp = Time, message = new { content = new[] { new { type = "tool_result", tool_use_id = 5, content = new { x = 1 } } } } }) +
                $$"""{"type":"user","timestamp":"{{Time}}","message":null}""" + "\n" +
                "[1]\nnull\n{not json\n" +
                User(new[] { Result("t1", "Passed!") }) +
                Assistant(Text("All green.")));
            var next = Assistant(Text("More to say here, at some length."));
            File.AppendAllText(path, next[..20]);

            for (var poll = 0; poll < 3; poll++)
            {
                var a = ActivityReader.Read(path);
                Assert.Equal(["prompt", "test", "read", "say"], a.Events.Select(e => e.K));
                Assert.True(a.Events[1].Ok);
                Assert.Null(a.Events[2].File);
                Assert.Equal(4, a.Version);
            }

            // The half-written line is read once it's finished, and only once.
            File.AppendAllText(path, next[20..]);
            Assert.Equal(5, ActivityReader.Read(path).Version);
            Assert.Equal(5, ActivityReader.Read(path).Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Top_level_folders_come_with_their_file_counts()
    {
        using var repo = new TempRepo();
        repo.Commit(("src/a.cs", "a"), ("src/b.cs", "b"), ("src/deep/c.cs", "c"), ("src/deep/down/d.cs", "d"), ("src/other/e.cs", "e"), ("docs/readme.md", "d"), ("root.txt", "r"));
        var folders = await GitService.GetTopFoldersAsync(Path.Combine(repo.Root, "src"));
        Assert.NotNull(folders);
        Assert.True(LlamaTree.Platform.PathText.SamePlace(repo.Root, folders.Root), $"{folders.Root} isn't {repo.Root}");
        Assert.Equal([("src", 5), ("(root)", 1), ("docs", 1)], folders.Folders.Select(r => (r.Name, r.Files)));
        // A top-level folder's own sub-folders, each counted with everything below it; files directly in it are no folder.
        Assert.Equal([("deep", 2), ("other", 1)], folders.Folders[0].Folders.Select(r => (r.Name, r.Files)));
        Assert.Empty(folders.Folders[2].Folders);
        Assert.Null(await GitService.GetTopFoldersAsync(Path.GetTempPath()));
    }
}
