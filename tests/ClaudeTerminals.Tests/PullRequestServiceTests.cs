using ClaudeTerminals.Git;

namespace ClaudeTerminals.Tests;

/// <summary>Which service hosts a repository, from its remote URL; this decides where a credential may ever be sent.</summary>
public sealed class PullRequestServiceTests
{
    [Theory]
    [InlineData("https://github.com/davellamachant/ClaudeTerminals.git")]
    [InlineData("https://github.com/davellamachant/ClaudeTerminals")]
    [InlineData("https://someone@github.com/davellamachant/ClaudeTerminals.git")]
    [InlineData("git@github.com:davellamachant/ClaudeTerminals.git")]
    [InlineData("ssh://git@github.com/davellamachant/ClaudeTerminals.git")]
    public void GitHub_remotes_are_recognised(string url)
    {
        var host = PullRequestService.ParseRemote(url)!;

        Assert.True(host.IsGitHub);
        Assert.Equal(("davellamachant", "ClaudeTerminals"), (host.Owner, host.Repo));
        Assert.Equal("https://github.com/davellamachant/ClaudeTerminals/pulls", host.PullRequestsUrl);
        Assert.Equal("https://github.com/davellamachant/ClaudeTerminals/compare/feat/projects?expand=1", host.NewPullRequestUrl("feat/projects"));
    }

    [Theory]
    [InlineData("https://dev.azure.com/llamachant/FSSWorkflow-Core/_git/FSSWorkflow-Core")]
    [InlineData("https://llamachant@dev.azure.com/llamachant/FSSWorkflow-Core/_git/FSSWorkflow-Core")]
    [InlineData("https://llamachant.visualstudio.com/FSSWorkflow-Core/_git/FSSWorkflow-Core")]
    [InlineData("https://llamachant.visualstudio.com/DefaultCollection/FSSWorkflow-Core/_git/FSSWorkflow-Core")]
    [InlineData("git@ssh.dev.azure.com:v3/llamachant/FSSWorkflow-Core/FSSWorkflow-Core")]
    public void Azure_DevOps_remotes_are_recognised(string url)
    {
        var host = PullRequestService.ParseRemote(url)!;

        Assert.False(host.IsGitHub);
        Assert.Equal("dev.azure.com", host.Host);
        Assert.Equal(("llamachant", "FSSWorkflow-Core", "FSSWorkflow-Core"), (host.Owner, host.Project, host.Repo));
        Assert.Equal("https://dev.azure.com/llamachant/FSSWorkflow-Core/_git/FSSWorkflow-Core/pullrequests", host.PullRequestsUrl);
    }

    [Fact]
    public void Azure_names_with_spaces_round_trip_through_the_urls()
    {
        var host = PullRequestService.ParseRemote("https://dev.azure.com/my%20org/My%20Project/_git/My%20Repo")!;

        Assert.Equal(("my org", "My Project", "My Repo"), (host.Owner, host.Project, host.Repo));
        Assert.Equal("https://dev.azure.com/my%20org/My%20Project/_git/My%20Repo", host.WebUrl);
    }

    [Theory]
    [InlineData("D:/Demo/remote.git")]
    [InlineData("https://gitlab.com/group/project.git")]
    [InlineData("https://github.com.evil.example/owner/repo.git")]
    [InlineData("https://evil.example/dev.azure.com/org/project/_git/repo")]
    [InlineData("http://github.com/owner/repo.git")]
    public void Other_hosts_and_look_alikes_are_not_recognised(string url) => Assert.Null(PullRequestService.ParseRemote(url));
}
