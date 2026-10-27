using ClaudeTerminals.Git;

namespace ClaudeTerminals.Tests;

/// <summary>What the Project view reads for each worktree, and Remove worktree's refusals.</summary>
public class WorktreeOverviewTests : IDisposable
{
    private readonly TempRepo repo = new();
    private readonly List<string> made = [];

    public WorktreeOverviewTests() => repo.Commit(("a.txt", "one"));

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

    private async Task<string> AddWorktree(string branch)
    {
        var path = NewFolder();
        var r = await GitService.AddWorktreeAsync(repo.Root, path, branch, "main");
        Assert.True(r.Ok, r.Error);
        return path;
    }

    [Fact]
    public async Task The_base_branch_is_origin_HEAD_else_a_local_main()
    {
        Assert.Equal("main", await GitService.GetBaseBranchAsync(repo.Root));
        // A remote named origin whose HEAD is known wins over the local branch.
        using var remote = new TempRepo();
        remote.Commit(("r.txt", "remote"));
        repo.Git("remote", "add", "origin", remote.Root);
        repo.Git("fetch", "-q", "origin");
        repo.Git("remote", "set-head", "origin", "main");
        Assert.Equal("origin/main", await GitService.GetBaseBranchAsync(repo.Root));
    }

    [Fact]
    public async Task No_base_branch_when_the_repository_has_neither()
    {
        using var other = new TempRepo();
        other.Commit(("a.txt", "one"));
        other.Git("checkout", "-q", "-b", "trunk");
        other.Git("branch", "-D", "main");
        Assert.Null(await GitService.GetBaseBranchAsync(other.Root));
        var overview = await GitService.GetWorktreeOverviewAsync(other.Root);
        Assert.Null(overview.BaseBranch);
        Assert.Equal(0, overview.Worktrees.Single().Ahead);
    }

    [Fact]
    public async Task Each_worktree_reports_how_far_it_is_from_main_and_what_is_changed()
    {
        var ahead = await AddWorktree("feature/ahead");
        File.WriteAllText(Path.Combine(ahead, "b.txt"), "two");
        Run(ahead, "add", "-A");
        Run(ahead, "commit", "-q", "-m", "b");
        File.WriteAllText(Path.Combine(ahead, "c.txt"), "untracked");
        var merged = await AddWorktree("feature/merged");
        repo.Commit(("a.txt", "one and more"));   // main moves on: both worktrees are now behind it

        var overview = await GitService.GetWorktreeOverviewAsync(ahead);   // any worktree finds the whole repository
        Assert.Equal("main", overview.BaseBranch);
        Assert.Equal(Path.GetFullPath(repo.Root), Path.GetFullPath(overview.Root), ignoreCase: true);

        var rows = overview.Worktrees;
        Assert.True(rows[0].Main);
        Assert.Equal("main", rows[0].Branch);
        Assert.False(rows[0].Merged);   // never offered for removal

        var a = rows.Single(w => w.Branch == "feature/ahead");
        Assert.False(a.Main);
        Assert.Equal((1, 1), (a.Ahead, a.Behind));
        Assert.Equal(1, a.Changes);   // the untracked file
        Assert.False(a.Merged);

        var m = rows.Single(w => w.Branch == "feature/merged");
        Assert.Equal((0, 1), (m.Ahead, m.Behind));
        Assert.Equal(0, m.Changes);
        Assert.True(m.Merged);
        Assert.False(m.Missing);
    }

    [Fact]
    public async Task A_worktree_whose_folder_is_gone_is_missing()
    {
        var path = await AddWorktree("feature/gone");
        Directory.Delete(path, true);
        var row = (await GitService.GetWorktreeOverviewAsync(repo.Root)).Worktrees.Single(w => w.Branch == "feature/gone");
        Assert.True(row.Missing);
        Assert.Equal(0, row.Changes);
        // Removing it prunes the entry; the branch stays.
        Assert.True((await GitService.RemoveWorktreeAsync(repo.Root, path)).Ok);
        Assert.DoesNotContain(await GitService.GetWorktreesAsync(repo.Root), w => w.Branch == "feature/gone");
        Assert.Contains("feature/gone", repo.Git("branch", "--list"));
    }

    [Fact]
    public async Task Remove_takes_a_clean_worktree_and_keeps_its_branch()
    {
        var path = await AddWorktree("feature/done");
        var r = await GitService.RemoveWorktreeAsync(repo.Root, path);
        Assert.True(r.Ok, r.Error);
        Assert.False(Directory.Exists(path));
        Assert.DoesNotContain(await GitService.GetWorktreesAsync(repo.Root), w => w.Branch == "feature/done");
        Assert.Contains("feature/done", repo.Git("branch", "--list"));
    }

    [Fact]
    public async Task Remove_refuses_a_worktree_with_uncommitted_changes_and_touches_nothing()
    {
        var path = await AddWorktree("feature/busy");
        File.WriteAllText(Path.Combine(path, "a.txt"), "edited");
        File.WriteAllText(Path.Combine(path, "new.txt"), "new");
        var r = await GitService.RemoveWorktreeAsync(repo.Root, path);
        Assert.False(r.Ok);
        Assert.Contains("2 files are changed or new", r.Error);
        Assert.Equal("edited", File.ReadAllText(Path.Combine(path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(path, "new.txt")));
        Assert.Contains(await GitService.GetWorktreesAsync(repo.Root), w => w.Branch == "feature/busy");
    }

    [Fact]
    public async Task Remove_refuses_the_main_worktree_and_folders_that_are_not_worktrees()
    {
        Assert.Contains("main worktree", (await GitService.RemoveWorktreeAsync(repo.Root, repo.Root)).Error);
        Assert.Contains("isn't a worktree", (await GitService.RemoveWorktreeAsync(repo.Root, Path.GetTempPath())).Error);
        Assert.Contains("full path", (await GitService.RemoveWorktreeAsync(repo.Root, "..\\somewhere")).Error);
        Assert.True(Directory.Exists(repo.Root));
    }

    private static void Run(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "Test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err}");
    }
}
