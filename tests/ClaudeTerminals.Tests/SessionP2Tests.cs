using System.Text;
using System.Text.Json;
using ClaudeTerminals.Git;
using ClaudeTerminals.Sessions;

namespace ClaudeTerminals.Tests;

/// <summary>
/// Review findings F14 (leaked git watchers), F15 (final output before the exit notice) and F18 (conversation titles).
/// Not run alongside other tests: they count live watchers and set CLAUDE_CONFIG_DIR for the whole process.
/// </summary>
[Collection(nameof(GitUndoTests))]
public sealed class SessionP2Tests : IDisposable
{
    private readonly TempRepo repo = new();
    private readonly string config = Path.Combine(Path.GetTempPath(), "ct-claude-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? previousConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

    public SessionP2Tests()
    {
        repo.Commit(("a.txt", "a"));
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", config);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousConfig);
        try { Directory.Delete(config, true); } catch (IOException) { }   // includes DirectoryNotFoundException
        repo.Dispose();
    }

    private TerminalSession Session(string command = "claude") =>
        new(new SessionInfo { Name = "test", Directory = repo.Root, Command = command });

    // ---- F14: no watcher outlives its session or a superseded setup

    [Fact]
    public async Task Overlapping_git_setups_leave_exactly_one_watcher_and_none_after_removal()
    {
        var baseline = GitWatcher.Live;
        for (var round = 0; round < 10; round++)
        {
            var s = Session();
            await Task.WhenAll(s.InitGitAsync(), s.InitGitAsync(), s.InitGitAsync());
            Assert.Equal(baseline + 1, GitWatcher.Live);
            s.Dispose();
            Assert.Equal(baseline, GitWatcher.Live);
        }
    }

    [Fact]
    public void Disposing_a_watcher_from_several_threads_at_once_counts_it_once()
    {
        var baseline = GitWatcher.Live;
        for (var round = 0; round < 50; round++)
        {
            var watcher = new GitWatcher(repo.Root, null);
            using var start = new Barrier(8);
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                start.SignalAndWait();   // release every Dispose together to widen the race
                watcher.Dispose();
            });
            Assert.Equal(baseline, GitWatcher.Live);
        }
    }

    [Fact]
    public async Task Removing_a_session_while_git_setup_is_running_leaves_no_watcher()
    {
        var baseline = GitWatcher.Live;
        var other = Session();   // another session keeps its own watcher
        await other.InitGitAsync();
        for (var round = 0; round < 10; round++)
        {
            var s = Session();
            var setup = s.InitGitAsync();
            s.Dispose();
            await setup;
            await s.InitGitAsync();   // a refresh arriving after removal does nothing either
        }
        Assert.Equal(baseline + 1, GitWatcher.Live);
        other.Dispose();
        Assert.Equal(baseline, GitWatcher.Live);
    }

    // ---- F15: everything a process printed arrives before its exit is reported

    private static async Task<(string Output, int Code)> RunToExit(TerminalSession s)
    {
        var output = new StringBuilder();
        var done = new TaskCompletionSource<(string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Output += (_, text) => { lock (output) output.Append(text); };
        s.Exited += (_, code, _) => { lock (output) done.TrySetResult((output.ToString(), code)); };
        s.Start(120, 30);
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Final_output_on_both_streams_and_without_a_newline_arrives_before_the_exit()
    {
        for (var round = 0; round < 10; round++)
        {
            using var s = Session("cmd /c \"echo FINAL-OUT& echo FINAL-ERR 1>&2& <nul set /p =NO-NEWLINE-END& exit 7\"");
            var (output, code) = await RunToExit(s);
            Assert.Equal(7, code);
            Assert.Contains("FINAL-OUT", output);
            Assert.Contains("FINAL-ERR", output);
            Assert.Contains("NO-NEWLINE-END", output);
        }
    }

    [Fact]
    public async Task A_large_final_burst_arrives_before_the_exit()
    {
        using var s = Session("cmd /c \"for /l %i in (1,1,3000) do @echo burst-line-%i\"");
        var (output, code) = await RunToExit(s);
        Assert.Equal(0, code);
        Assert.Contains("burst-line-3000", output);
    }

    // ---- F18: the latest title and prompt, whatever the transcript's size

    /// <param name="latestFirst">Put the latest title and last prompt right after the first prompt, before the padding.</param>
    private string Transcript(string id, int padToBytes, bool latestFirst)
    {
        var lines = new List<object>
        {
            new { type = "user", sessionId = id, cwd = @"C:\work\x", gitBranch = "main", message = new { role = "user", content = "the first prompt" } },
            new { type = "custom-title", customTitle = "Early title" },
        };
        object[] latest =
        [
            new { type = "user", message = new { role = "user", content = "a later prompt" } },
            new { type = "custom-title", customTitle = "Latest title — 最新" },
            new { type = "last-prompt", lastPrompt = "the latest prompt" },
        ];
        if (latestFirst) lines.AddRange(latest);
        var filler = new { type = "assistant", message = new { role = "assistant", content = new string('x', 900) + " 日本語" } };
        var size = lines.Sum(l => JsonSerializer.Serialize(l).Length + 1);
        while (size < padToBytes) { lines.Add(filler); size += JsonSerializer.Serialize(filler).Length + 1; }
        if (!latestFirst) lines.AddRange(latest);

        var dir = Path.Combine(config, "projects", "C--work-x");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{id}.jsonl");
        File.WriteAllLines(file, lines.Select(l => JsonSerializer.Serialize(l)));
        return file;
    }

    [Theory]
    [InlineData(0, false)]          // tiny
    [InlineData(90_000, false)]     // just under the 96 KB head read
    [InlineData(100_000, false)]    // just over it
    [InlineData(400_000, false)]    // well over it (head + tail reads)
    [InlineData(100_000, true)]     // just over it, with the latest records near the start and padding after
    public void The_picker_gets_the_latest_title_and_prompt(int padToBytes, bool latestFirst)
    {
        var id = $"s-{padToBytes}-{latestFirst}";
        Transcript(id, padToBytes, latestFirst);

        var c = Assert.Single(ConversationIndex.List(null, 50), x => x.SessionId == id);

        Assert.Equal("Latest title — 最新", c.Title);
        Assert.Equal("the latest prompt", c.LastPrompt);
        Assert.Equal("the first prompt", c.FirstPrompt);
    }

    // ---- a session's own conversation, for its tooltips

    [Fact]
    public void A_sessions_conversation_is_found_by_its_id_with_when_it_started()
    {
        var id = Guid.NewGuid().ToString();
        var dir = Path.Combine(config, "projects", "C--work-x");
        Directory.CreateDirectory(dir);
        object[] lines =
        [
            new { type = "user", sessionId = id, cwd = @"C:\work\x", timestamp = "2026-10-22T08:30:12.345Z", message = new { role = "user", content = "the first prompt" } },
            new { type = "ai-title", aiTitle = "Fix the login page" },
        ];
        File.WriteAllLines(Path.Combine(dir, $"{id}.jsonl"), lines.Select(l => JsonSerializer.Serialize(l)));

        var c = ConversationIndex.Find(id, @"C:\work\x");

        Assert.NotNull(c);
        Assert.Equal("Fix the login page", c.Title);
        Assert.Equal(new DateTime(2026, 10, 22, 8, 30, 12, 345, DateTimeKind.Utc), c.Started);
        Assert.Null(ConversationIndex.Find(Guid.NewGuid().ToString(), @"C:\work\x"));   // nothing saved in it yet
    }
}
