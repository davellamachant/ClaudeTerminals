using LlamaTree.Git;

namespace LlamaTree.Tests;

/// <summary>
/// Commits, branches and remote names that start with "-" reach git as values, never as options: each call puts
/// --end-of-options before them (or refuses them where git has nothing to separate them with).
/// </summary>
public class GitEndOfOptionsTests : IDisposable
{
    private readonly TempRepo repo = new();
    private readonly string outside = Path.Combine(Path.GetTempPath(), "ct-eoo-" + Guid.NewGuid().ToString("N")[..8]);

    public GitEndOfOptionsTests()
    {
        repo.Commit(("a.txt", "one\n"));
        repo.Commit(("a.txt", "two\n"), ("b.txt", "b\n"));
    }

    public void Dispose()
    {
        repo.Dispose();
        try { if (File.Exists(outside)) File.Delete(outside); else if (Directory.Exists(outside)) FileSystemFixtures.DeleteTree(outside); }
        catch (IOException) { }
    }

    private string Head() => repo.Git("rev-parse", "HEAD").Trim();

    [Fact]
    public async Task Commit_details_of_an_option_shaped_sha_write_nothing()
    {
        await GitService.GetCommitDetailsAsync(repo.Root, "--output=" + outside);

        Assert.False(File.Exists(outside), "git show took the sha as --output");
    }

    [Fact]
    public async Task Commit_details_still_list_the_files()
    {
        var details = await GitService.GetCommitDetailsAsync(repo.Root, Head());

        var files = (List<FileChange>)details.GetType().GetProperty("files")!.GetValue(details)!;
        Assert.Equal(["a.txt", "b.txt"], files.Select(f => f.Path).Order());
    }

    [Fact]
    public async Task Commit_diff_of_an_option_shaped_commit_shows_nothing()
    {
        var diff = await GitService.GetDiffAsync(repo.Root, "commit", "a.txt", null, "--format=format:injected", null);

        Assert.Equal("", diff.Modified);   // as an option, git would have shown HEAD in that format
    }

    [Fact]
    public async Task Commit_diff_still_shows_both_sides()
    {
        var head = Head();
        var diff = await GitService.GetDiffAsync(repo.Root, "commit", "a.txt", null, head, head + "~1");

        Assert.Equal(("one\n", "two\n"), (diff.Original, diff.Modified));
    }

    [Fact]
    public async Task Remote_branch_whose_local_name_starts_with_a_dash_is_refused()
    {
        repo.Git("remote", "add", "origin", "https://example.invalid/repo.git");
        repo.Git("update-ref", "refs/remotes/origin/-c", "HEAD");

        var r = await GitService.CheckoutBranchAsync(repo.Root, "origin/-c", remote: true, stashChanges: false);

        Assert.False(r.Ok);
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
        Assert.DoesNotContain("-c", repo.Git("for-each-ref", "--format=%(refname)", "refs/heads"));
    }

    [Fact]
    public async Task New_branch_from_an_option_shaped_start_point_is_refused()
    {
        var r = await GitService.CreateBranchAsync(repo.Root, "x", "--orphan", checkout: true, stashChanges: false);

        Assert.False(r.Ok);
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
    }

    [Fact]
    public async Task New_branch_from_a_start_point_still_works()
    {
        var made = await GitService.CreateBranchAsync(repo.Root, "older", "HEAD~1", checkout: false, stashChanges: false);
        var switched = await GitService.CreateBranchAsync(repo.Root, "older2", "HEAD~1", checkout: true, stashChanges: false);

        Assert.True(made.Ok, made.Message);
        Assert.True(switched.Ok, switched.Message);
        Assert.Equal("older2", repo.Git("branch", "--show-current").Trim());
        Assert.Equal("one\n", repo.Read("a.txt"));
    }

    [Fact]
    public async Task Push_publishes_to_a_remote_whose_name_looks_like_an_option()
    {
        repo.Git("init", "-q", "--bare", outside);
        repo.Git("config", "remote.--dry-run.url", outside);   // as a flag it would push nowhere; as a name it is that repository

        var r = await GitService.PushAsync(repo.Root);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(Head(), repo.Git("--git-dir", outside, "rev-parse", "refs/heads/main").Trim());
    }

    [Fact]
    public async Task Pull_request_host_comes_from_a_remote_whose_name_looks_like_an_option()
    {
        repo.Git("config", "remote.--push.url", "https://github.com/owner/repo.git");
        repo.Git("config", "branch.main.remote", "--push");

        var host = await PullRequestService.GetHostAsync(repo.Root, "main");

        Assert.Equal(("github", "owner", "repo"), (host?.Kind, host?.Owner, host?.Repo));
    }
}
