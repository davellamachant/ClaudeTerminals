using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaTree.Git;

/// <summary>Where a repository is hosted, from its remote URL; null for hosts this doesn't know.</summary>
internal sealed record RepoHost(string Kind, string Host, string Owner, string Project, string Repo)
{
    public bool IsGitHub => Kind == "github";

    /// <summary>The repository's web page, its pull request list, and the page to open a new one from a branch.</summary>
    public string WebUrl => IsGitHub
        ? $"https://github.com/{Owner}/{Repo}"
        : $"https://dev.azure.com/{Esc(Owner)}/{Esc(Project)}/_git/{Esc(Repo)}";

    public string PullRequestsUrl => IsGitHub ? $"{WebUrl}/pulls" : $"{WebUrl}/pullrequests";

    public string NewPullRequestUrl(string branch) => IsGitHub
        ? $"{WebUrl}/compare/{Uri.EscapeDataString(branch).Replace("%2F", "/")}?expand=1"
        : $"{WebUrl}/pullrequestcreate?sourceRef={Uri.EscapeDataString(branch)}";

    private static string Esc(string s) => Uri.EscapeDataString(s);
}

internal sealed record PullRequestInfo(int Number, string Title, string Url, bool IsDraft);

/// <summary>
/// Finds the open pull request for a branch on GitHub or Azure DevOps, like Visual Studio's "PR 958" chip. Credentials
/// come from Git Credential Manager without ever prompting, and are only sent to the API host of the repository's own
/// service over HTTPS.
/// </summary>
internal static partial class PullRequestService
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    // github.com/owner/repo(.git) over https or ssh
    [GeneratedRegex(@"^(?:https://(?:[^@/]+@)?github\.com/|git@github\.com:|ssh://git@github\.com/)(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrl();

    // dev.azure.com/org/project/_git/repo, org.visualstudio.com/(DefaultCollection/)project/_git/repo, ssh v3 form
    [GeneratedRegex(@"^https://(?:[^@/]+@)?dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AzureUrl();

    [GeneratedRegex(@"^https://(?:[^@/]+@)?(?<org>[^./]+)\.visualstudio\.com/(?:DefaultCollection/)?(?<project>[^/]+)/_git/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VisualStudioUrl();

    [GeneratedRegex(@"^(?:git@|ssh://git@)ssh\.dev\.azure\.com(?::v3/|/v3/)(?<org>[^/]+)/(?<project>[^/]+)/(?<repo>[^/]+?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex AzureSshUrl();

    /// <remarks>
    /// The names end up in credential fill's input, one attribute per line, so one holding a control character (a
    /// line break, raw or escaped as %0A) is refused outright rather than cleaned up.
    /// </remarks>
    public static RepoHost? ParseRemote(string url)
    {
        url = url.Trim();
        if (GitHubUrl().Match(url) is { Success: true } g)
        {
            var (owner, repo) = (g.Groups["owner"].Value, g.Groups["repo"].Value);
            return HasControl(owner, repo) ? null : new RepoHost("github", "github.com", owner, "", repo);
        }
        foreach (var re in new[] { AzureUrl(), VisualStudioUrl(), AzureSshUrl() })
            if (re.Match(url) is { Success: true } a)
            {
                var (org, project, repo) = (Uri.UnescapeDataString(a.Groups["org"].Value), Uri.UnescapeDataString(a.Groups["project"].Value),
                    Uri.UnescapeDataString(a.Groups["repo"].Value));
                return HasControl(org, project, repo) ? null : new RepoHost("azure", "dev.azure.com", org, project, repo);
            }
        return null;
    }

    /// <summary>The host of the branch's remote (its upstream's, else origin), or null if it isn't GitHub or Azure DevOps.</summary>
    public static async Task<RepoHost?> GetHostAsync(string root, string? branch)
    {
        var remote = branch != null ? (await GitService.RunAsync(root, ["config", "--get", $"branch.{branch}.remote"])).Text.Trim() : "";
        if (remote.Length == 0 || remote == ".") remote = "origin";
        var url = await GitService.RunAsync(root, ["remote", "get-url", "--end-of-options", remote]);
        return url.Ok ? ParseRemote(url.Text) : null;
    }

    /// <summary>The open pull request whose source is <paramref name="branch"/>, or null if there is none or it can't be asked.</summary>
    public static async Task<PullRequestInfo?> FindAsync(RepoHost host, string branch)
    {
        var token = await GetTokenAsync(host);
        return host.IsGitHub ? await FindOnGitHubAsync(host, branch, token) : await FindOnAzureAsync(host, branch, token);
    }

    private static async Task<PullRequestInfo?> FindOnGitHubAsync(RepoHost host, string branch, string? token)
    {
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(host.Owner)}/{Uri.EscapeDataString(host.Repo)}/pulls" +
                  $"?state=open&per_page=5&head={Uri.EscapeDataString(host.Owner + ":" + branch)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("LlamaTree");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var pr in doc.RootElement.EnumerateArray())
            return new PullRequestInfo(pr.GetProperty("number").GetInt32(), pr.GetProperty("title").GetString() ?? "",
                pr.GetProperty("html_url").GetString() ?? host.PullRequestsUrl, pr.TryGetProperty("draft", out var d) && d.GetBoolean());
        return null;
    }

    private static async Task<PullRequestInfo?> FindOnAzureAsync(RepoHost host, string branch, string? token)
    {
        if (token == null) return null;   // Azure DevOps has no anonymous API for private projects
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(host.Owner)}/{Uri.EscapeDataString(host.Project)}/_apis/git/repositories/" +
                  $"{Uri.EscapeDataString(host.Repo)}/pullrequests?searchCriteria.status=active" +
                  $"&searchCriteria.sourceRefName={Uri.EscapeDataString("refs/heads/" + branch)}&$top=5&api-version=7.1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("LlamaTree");
        // Git Credential Manager hands out either an OAuth access token (a JWT) or a personal access token.
        request.Headers.Authorization = token.Count(c => c == '.') == 2
            ? new AuthenticationHeaderValue("Bearer", token)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var pr in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var id = pr.GetProperty("pullRequestId").GetInt32();
            return new PullRequestInfo(id, pr.GetProperty("title").GetString() ?? "", $"{host.WebUrl}/pullrequest/{id}",
                pr.TryGetProperty("isDraft", out var d) && d.GetBoolean());
        }
        return null;
    }

    internal static readonly string[] CredentialFillArgs = ["-c", "credential.interactive=false", "credential", "fill"];

    internal static readonly IReadOnlyDictionary<string, string?> CredentialEnvironment = new Dictionary<string, string?>
    {
        ["GCM_INTERACTIVE"] = "never", ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_ASKPASS"] = "", ["SSH_ASKPASS"] = "",
    };

    /// <summary>
    /// What credential fill is asked, one attribute per line; null if a part of the host would break a line (a control
    /// character, which <see cref="ParseRemote"/> already refuses) and so could add attributes of its own.
    /// </summary>
    internal static string? CredentialInput(RepoHost host)
    {
        if (HasControl(host.Host, host.Owner, host.Project, host.Repo)) return null;
        var path = host.IsGitHub ? "" : $"path={host.Owner}/{host.Project}/_git/{host.Repo}\n";
        return $"protocol=https\nhost={host.Host}\n{path}\n";
    }

    private static bool HasControl(params string[] parts) => parts.Any(p => p.Any(char.IsControl));

    /// <summary>
    /// Asks Git Credential Manager for the stored credential of this host without any sign-in prompt (it fails instead),
    /// and never stores or rejects anything. Null when there is none. This runs without the person doing anything, so
    /// git runs outside the repository (<see cref="GitService.RunOutsideRepositoryAsync"/>) and reads only their own
    /// config: a repository's credential.helper can name any command.
    /// </summary>
    private static async Task<string?> GetTokenAsync(RepoHost host)
    {
        if (CredentialInput(host) is not { } input) return null;
        var result = await GitService.RunOutsideRepositoryAsync(CredentialFillArgs, input, CredentialEnvironment, TimeSpan.FromSeconds(20));
        if (!result.Ok) return null;
        foreach (var line in result.Text.Split('\n'))
            if (line.StartsWith("password=", StringComparison.Ordinal))
                return line[9..].Trim() is { Length: > 0 } token ? token : null;
        return null;
    }
}
