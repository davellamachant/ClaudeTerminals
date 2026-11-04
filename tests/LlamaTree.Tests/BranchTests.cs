using LlamaTree.Git;

namespace LlamaTree.Tests;

/// <summary>New Branch in the Git panels: made from a branch or commit, switched to, with changes brought along or stashed.</summary>
public class BranchTests : IDisposable
{
    private readonly TempRepo repo = new();

    public BranchTests()
    {
        repo.Commit(("a.txt", "one\n"), ("b.txt", "b\n"));
        repo.Git("branch", "other");
        repo.Commit(("a.txt", "two\n"));   // main and other now differ in a.txt
    }

    public void Dispose() => repo.Dispose();

    private string Head() => repo.Git("branch", "--show-current").Trim();

    private string Stashes() => repo.Git("stash", "list").Trim();

    /// <summary>A remote named origin with the given branches at HEAD; no network involved.</summary>
    private void FakeRemote(params string[] branches)
    {
        repo.Git("remote", "add", "origin", "https://example.invalid/repo.git");
        foreach (var b in branches) repo.Git("update-ref", "refs/remotes/origin/" + b, "HEAD");
    }

    [Fact]
    public async Task Branches_lists_local_then_remote_with_tracking()
    {
        FakeRemote("main", "feature/y");
        repo.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        repo.Git("branch", "--set-upstream-to=origin/main", "main");
        repo.Commit(("c.txt", "c\n"));   // main is one ahead of origin/main

        var list = await GitService.GetBranchesAsync(repo.Root);

        Assert.Equal(["main", "other", "origin/feature/y", "origin/main"], list.Select(b => b.Name));
        Assert.Equal([false, false, true, true], list.Select(b => b.Remote));
        var main = list[0];
        Assert.Equal(("origin/main", 1, 0), (main.Upstream, main.Ahead, main.Behind));
        Assert.Equal(Path.GetFullPath(repo.Root).TrimEnd('\\'), Path.GetFullPath(main.Worktree!).TrimEnd('\\'), ignoreCase: true);
        Assert.Null(list[1].Upstream);
        Assert.Null(list[1].Worktree);
    }

    [Fact]
    public async Task Checkout_switches_to_a_local_branch_bringing_changes()
    {
        repo.Write("b.txt", "edited\n");

        var r = await GitService.CheckoutBranchAsync(repo.Root, "other", remote: false, stashChanges: false);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("other", Head());
        Assert.Equal("one\n", repo.Read("a.txt"));
        Assert.Equal("edited\n", repo.Read("b.txt"));
    }

    [Fact]
    public async Task Checkout_of_a_remote_branch_makes_a_tracking_local_branch()
    {
        FakeRemote("feature/y");

        var r = await GitService.CheckoutBranchAsync(repo.Root, "origin/feature/y", remote: true, stashChanges: false);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("feature/y", Head());
        Assert.Equal("origin/feature/y", (await GitService.GetStatusAsync(repo.Root)).Upstream);
    }

    [Fact]
    public async Task Checkout_of_a_remote_branch_uses_the_existing_local_one()
    {
        FakeRemote("other");
        repo.Git("switch", "-q", "-c", "elsewhere");

        var r = await GitService.CheckoutBranchAsync(repo.Root, "origin/other", remote: true, stashChanges: false);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("other", Head());
        Assert.Equal("one\n", repo.Read("a.txt"));   // the local other, not origin/other (which is at main's commit)
    }

    [Fact]
    public async Task Checkout_with_stash_leaves_the_changes_stashed()
    {
        repo.Write("a.txt", "edited\n");

        var r = await GitService.CheckoutBranchAsync(repo.Root, "other", remote: false, stashChanges: true);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("other", Head());
        Assert.Equal("one\n", repo.Read("a.txt"));
        Assert.Contains("before switching to other", Stashes());
    }

    [Theory]
    [InlineData("-f", false)]
    [InlineData("nowhere", false)]
    [InlineData("origin/nowhere", true)]
    public async Task Checkout_of_an_unknown_branch_changes_nothing(string branch, bool remote)
    {
        repo.Write("a.txt", "edited\n");
        var before = repo.Snapshot();

        var r = await GitService.CheckoutBranchAsync(repo.Root, branch, remote, stashChanges: true);

        Assert.False(r.Ok);
        Assert.Equal(before, repo.Snapshot());
        Assert.Equal("", Stashes());
    }

    [Fact]
    public async Task Changes_come_along_to_the_new_branch()
    {
        repo.Write("b.txt", "edited\n");
        repo.Write("new.txt", "new\n");

        var r = await GitService.CreateBranchAsync(repo.Root, "feature/x", null, checkout: true, stashChanges: false);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("feature/x", Head());
        Assert.Equal("edited\n", repo.Read("b.txt"));
        Assert.Equal("new\n", repo.Read("new.txt"));
        Assert.Equal("", Stashes());
    }

    [Fact]
    public async Task Stashed_changes_stay_behind_and_the_tree_is_clean()
    {
        repo.Write("a.txt", "edited\n");
        repo.Write("new.txt", "new\n");

        var r = await GitService.CreateBranchAsync(repo.Root, "feature/x", "other", checkout: true, stashChanges: true);

        Assert.True(r.Ok, r.Message);
        Assert.NotNull(r.Stash);
        Assert.Equal("feature/x", Head());
        Assert.Equal("one\n", repo.Read("a.txt"));
        Assert.False(repo.Exists("new.txt"));
        Assert.Contains("before switching to feature/x", Stashes());
        Assert.Equal(r.Stash, repo.Git("rev-parse", "stash@{0}").Trim());
    }

    [Fact]
    public async Task Changes_that_would_be_overwritten_refuse_and_change_nothing()
    {
        repo.Write("a.txt", "edited\n");   // a.txt differs between main and other
        var before = repo.Snapshot();

        var r = await GitService.CreateBranchAsync(repo.Root, "feature/x", "other", checkout: true, stashChanges: false);

        Assert.False(r.Ok);
        Assert.Contains("stash them instead", r.Message);
        Assert.Equal(before, repo.Snapshot());
        Assert.Throws<InvalidOperationException>(() => repo.Git("rev-parse", "--verify", "--quiet", "refs/heads/feature/x"));
    }

    [Fact]
    public async Task Without_checkout_the_branch_is_made_and_nothing_else_changes()
    {
        repo.Write("a.txt", "edited\n");
        var before = repo.Snapshot();

        var r = await GitService.CreateBranchAsync(repo.Root, "feature/x", "other", checkout: false, stashChanges: true);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(before, repo.Snapshot());
        Assert.Equal(repo.Git("rev-parse", "other").Trim(), repo.Git("rev-parse", "feature/x").Trim());
    }

    [Theory]
    [InlineData("-D", null)]
    [InlineData("bad..name", null)]
    [InlineData("other", null)]          // exists
    [InlineData("feature/x", "--orphan")]
    [InlineData("feature/x", "nowhere")]
    public async Task Bad_names_and_start_points_are_refused(string branch, string? startPoint)
    {
        repo.Write("a.txt", "edited\n");
        var before = repo.Snapshot();

        var r = await GitService.CreateBranchAsync(repo.Root, branch, startPoint, checkout: true, stashChanges: true);

        Assert.False(r.Ok);
        Assert.Equal(before, repo.Snapshot());
        Assert.Equal("", Stashes());
    }

    [Fact]
    public async Task A_new_branch_from_a_remote_branch_tracks_nothing()
    {
        repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");

        var r = await GitService.CreateBranchAsync(repo.Root, "feature/x", "origin/main", checkout: true, stashChanges: false);

        Assert.True(r.Ok, r.Message);
        Assert.Null((await GitService.GetStatusAsync(repo.Root)).Upstream);
    }
}
