using ClaudeTerminals.Git;
using ClaudeTerminals.Sessions;

namespace ClaudeTerminals.Tests;

/// <summary>A session in a folder above its repositories can show and switch between the ones inside it.</summary>
[Collection(nameof(GitUndoTests))]   // sessions install watchers, which SessionP2Tests counts
public sealed class NestedRepoTests : IDisposable
{
    private readonly string parent = Path.Combine(Path.GetTempPath(), "ct-nested-" + Guid.NewGuid().ToString("N")[..8]);

    public NestedRepoTests() => Directory.CreateDirectory(parent);

    public void Dispose()
    {
        try { FileSystemFixtures.DeleteTree(parent); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<string> InitRepo(string relative)
    {
        var dir = Path.Combine(parent, relative);
        Directory.CreateDirectory(dir);
        Assert.True((await GitService.RunAsync(dir, ["init", "-q", "-b", "main"])).Ok);
        return Path.GetFullPath(dir);
    }

    [Fact]
    public async Task Finds_repositories_in_folders_below_but_not_in_skipped_ones()
    {
        var a = await InitRepo("a");
        var b = await InitRepo(Path.Combine("group", "b"));
        await InitRepo(Path.Combine("node_modules", "pkg"));
        await InitRepo(Path.Combine(".hidden", "c"));
        var inner = await InitRepo(Path.Combine("a", "libs", "inner"));   // a repository inside another

        var found = await GitService.FindRepositoriesAsync(parent);

        Assert.Equal([a, inner, b], found);
    }

    [Fact]
    public async Task Stops_at_the_depth_limit()
    {
        await InitRepo(Path.Combine("1", "2", "3", "deep"));
        Assert.Empty(await GitService.FindRepositoriesAsync(parent, maxDepth: 3));
        Assert.Single(await GitService.FindRepositoriesAsync(parent, maxDepth: 4));
    }

    [Fact]
    public async Task Lists_the_containing_repository_first()
    {
        var outer = await InitRepo("outer");
        var sub = Directory.CreateDirectory(Path.Combine(outer, "src")).FullName;
        var nested = await InitRepo(Path.Combine("outer", "src", "nested"));

        var found = await GitService.FindRepositoriesAsync(sub);

        Assert.Equal(2, found.Count);
        Assert.Equal(outer, found[0], ignoreCase: true);
        Assert.Equal(nested, found[1]);
    }

    [Fact]
    public async Task A_session_above_repositories_shows_the_first_and_can_switch_to_another()
    {
        var a = await InitRepo("a");
        var b = await InitRepo("b");
        File.WriteAllText(Path.Combine(b, "new.txt"), "x");
        var info = new SessionInfo { Name = "test", Directory = parent };
        using var s = new TerminalSession(info);
        GitStatus? last = null;
        s.StatusChanged += (_, st) => last = st;

        await s.InitGitAsync();
        Assert.Equal(a, s.RepoRoot);
        Assert.Equal([a, b], s.Repositories);
        Assert.True(last!.IsRepo);
        Assert.Empty(last.Unstaged);

        Assert.True(await s.SelectRepositoryAsync(b.ToUpperInvariant()));
        Assert.Equal(b, s.RepoRoot);
        Assert.Equal(b, info.GitRepo);
        Assert.Equal("new.txt", Assert.Single(last!.Unstaged).Path);

        // Picking the default again forgets the choice; something that isn't one of the repositories is refused.
        Assert.True(await s.SelectRepositoryAsync(a));
        Assert.Null(info.GitRepo);
        Assert.False(await s.SelectRepositoryAsync(parent));
        Assert.Equal(a, s.RepoRoot);
    }

    [Fact]
    public void A_remembered_repository_that_is_gone_falls_back_to_the_first()
    {
        Assert.Equal(@"C:\r\b", TerminalSession.ChooseRepository([@"C:\r\a", @"C:\r\b"], @"c:\R\B"));
        Assert.Equal(@"C:\r\a", TerminalSession.ChooseRepository([@"C:\r\a", @"C:\r\b"], @"C:\r\gone"));
        Assert.Null(TerminalSession.ChooseRepository([], @"C:\r\a"));
    }
}
