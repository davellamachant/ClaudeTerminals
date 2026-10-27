using ClaudeTerminals.Git;

namespace ClaudeTerminals.Tests;

/// <summary>Creating a worktree on a new branch for a new session.</summary>
public class WorktreeTests : IDisposable
{
    private readonly TempRepo repo = new();
    private readonly List<string> made = [];

    public WorktreeTests() => repo.Commit(("a.txt", "one"));

    public void Dispose()
    {
        foreach (var dir in made)
            try { Directory.Delete(dir, true); } catch (DirectoryNotFoundException) { }
        repo.Dispose();
    }

    private string NewFolder()
    {
        var path = repo.Root + "-wt-" + Guid.NewGuid().ToString("N")[..6];
        made.Add(path);
        return path;
    }

    [Fact]
    public async Task A_worktree_is_made_on_a_new_branch()
    {
        var path = NewFolder();
        var r = await GitService.AddWorktreeAsync(repo.Root, path, "feature/thing", "main");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("one", File.ReadAllText(Path.Combine(path, "a.txt")));
        var worktrees = await GitService.GetWorktreesAsync(repo.Root);
        Assert.Contains(worktrees, w => w.Branch == "feature/thing" && string.Equals(Path.GetFullPath(w.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Without_a_start_point_it_starts_from_the_current_commit()
    {
        var path = NewFolder();
        Assert.True((await GitService.AddWorktreeAsync(repo.Root, path, "next", null)).Ok);
        Assert.Equal(repo.Git("rev-parse", "main").Trim(), repo.Git("rev-parse", "next").Trim());
    }

    [Fact]
    public async Task A_branch_started_from_a_remote_branch_has_no_upstream_until_pushed()
    {
        // As the Project view's New worktree does, from origin/main: without --no-track git would make origin/main the
        // new branch's upstream, and Push would then fail on the names not matching.
        repo.Git("remote", "add", "origin", repo.Root + "-origin");
        repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        var path = NewFolder();
        Assert.True((await GitService.AddWorktreeAsync(repo.Root, path, "feature/from-origin", "origin/main")).Ok);
        Assert.Null((await GitService.GetStatusAsync(path)).Upstream);
    }

    [Theory]
    [InlineData("bad name", "isn't a valid branch name")]
    [InlineData("--force", "isn't a valid branch name")]          // never an option
    [InlineData("main", "already exists")]
    public async Task Bad_branch_names_are_refused(string branch, string message)
    {
        var r = await GitService.AddWorktreeAsync(repo.Root, NewFolder(), branch, null);
        Assert.False(r.Ok);
        Assert.Contains(message, r.Error);
    }

    [Fact]
    public async Task Bad_folders_and_start_points_are_refused()
    {
        Assert.Contains("full path", (await GitService.AddWorktreeAsync(repo.Root, "relative\\dir", "x1", null)).Error);
        Assert.Contains("isn't empty", (await GitService.AddWorktreeAsync(repo.Root, repo.Root, "x2", null)).Error);
        Assert.Contains("isn't a branch or commit", (await GitService.AddWorktreeAsync(repo.Root, NewFolder(), "x3", "--orphan")).Error);
        Assert.Contains("isn't a branch or commit", (await GitService.AddWorktreeAsync(repo.Root, NewFolder(), "x4", "no-such-branch")).Error);
        Assert.DoesNotContain("x3", repo.Git("branch", "--list"));   // nothing was created
    }
}
