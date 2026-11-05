using LlamaTree.Git;

namespace LlamaTree.Tests;

/// <summary>
/// Security finding H3 (and M1): the app's own git calls, most of them automatic, never run a command a repository's
/// config names (core.fsmonitor, diff textconv, credential.helper), and never send a file an untracked link leads to.
/// </summary>
public sealed class GitConfigSafetyTests : IDisposable
{
    private readonly TempRepo repo = new();
    private readonly string outside = Path.Combine(Path.GetTempPath(), "ct-outside-" + Guid.NewGuid().ToString("N")[..8]);

    public GitConfigSafetyTests() => Directory.CreateDirectory(outside);

    public void Dispose()
    {
        repo.Dispose();
        try { FileSystemFixtures.DeleteTree(outside); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A shell command (git runs these through sh, on Windows too) that leaves <paramref name="marker"/> behind.</summary>
    private static string Touch(string marker) => $"echo ran > '{marker.Replace('\\', '/')}'";

    // ---- The central runner

    [Fact]
    public void Every_call_turns_off_the_fsmonitor_untracked_cache_signatures_and_pager_before_its_own_arguments()
    {
        var psi = GitService.CreateStartInfo(repo.Root, ["status", "--porcelain=v2"]);
        var args = psi.ArgumentList.ToList();

        Assert.Equal(GitService.SafeConfig, args.Take(GitService.SafeConfig.Length));
        Assert.Equal(new[] { "status", "--porcelain=v2" }, args.Skip(GitService.SafeConfig.Length));
        Assert.Contains("--no-pager", args);
        foreach (var setting in new[] { "core.fsmonitor=false", "core.untrackedCache=false", "log.showSignature=false" })
            Assert.Equal("-c", args[args.IndexOf(setting) - 1]);
        Assert.Equal("cat", psi.Environment["GIT_PAGER"]);
        Assert.Equal(repo.Root, psi.WorkingDirectory);
    }

    [Fact]
    public void A_null_environment_value_removes_the_variable()
    {
        var psi = GitService.CreateStartInfo(repo.Root, ["status"], environment: new Dictionary<string, string?> { ["GIT_DIR"] = null, ["X_SET"] = "1" });
        Assert.False(psi.Environment.ContainsKey("GIT_DIR"));
        Assert.Equal("1", psi.Environment["X_SET"]);
    }

    [Fact]
    public async Task Status_doesnt_run_the_repositorys_fsmonitor_command()
    {
        repo.Commit(("a.txt", "one\n"));
        var marker = Path.Combine(outside, "fsmonitor-ran");
        repo.Git("config", "core.fsmonitor", Touch(marker));
        repo.Write("a.txt", "two\n");
        // The trap works: plain git status runs it.
        repo.Git("status", "--porcelain");
        Assert.True(File.Exists(marker), "git didn't run core.fsmonitor at all, so this test proves nothing");
        File.Delete(marker);

        var status = await GitService.GetStatusAsync(repo.Root);
        var overview = await GitService.GetWorktreeOverviewAsync(repo.Root);

        Assert.True(status.IsRepo, status.Error);
        Assert.Contains(status.Unstaged, f => f.Path == "a.txt");
        Assert.Single(overview.Worktrees);
        Assert.False(File.Exists(marker), "the app's git status ran the repository's core.fsmonitor command");
    }

    [Fact]
    public async Task Diffs_the_app_reads_dont_run_the_repositorys_textconv_or_external_diff()
    {
        repo.Commit(("a.txt", "one\n"));
        var textconv = Path.Combine(outside, "textconv-ran");
        var external = Path.Combine(outside, "external-ran");
        repo.Write(".git/info/attributes", "*.txt diff=trap\n");
        repo.Git("config", "diff.trap.textconv", Touch(textconv) + "; cat");
        repo.Write("a.txt", "two\n");
        repo.Git("diff");
        Assert.True(File.Exists(textconv), "git didn't run the textconv at all, so this test proves nothing");
        File.Delete(textconv);
        repo.Git("config", "diff.external", Touch(external));

        var prompt = await CommitMessageService.BuildPromptAsync(repo.Root);
        repo.Git("add", "a.txt");
        var staged = await CommitMessageService.BuildPromptAsync(repo.Root);
        var shown = await GitService.ShowAsync(repo.Root, ":a.txt");

        Assert.Contains("+two", prompt);
        Assert.Contains("+two", staged);
        Assert.Equal("two\n", System.Text.Encoding.UTF8.GetString(shown.Bytes));
        Assert.False(File.Exists(textconv), "a diff ran the repository's textconv");
        Assert.False(File.Exists(external), "a diff ran the repository's diff.external");
    }

    // ---- Credential fill

    [Fact]
    public void Credential_fill_starts_in_an_empty_folder_with_the_repository_variables_cleared()
    {
        var neutral = Directory.CreateTempSubdirectory("ct-neutral-").FullName;
        try
        {
            // Even a caller asking for GIT_DIR can't point it back at a repository.
            var env = GitService.OutsideRepositoryEnvironment(neutral,
                new Dictionary<string, string?>(PullRequestService.CredentialEnvironment) { ["GIT_DIR"] = Path.Combine(repo.Root, ".git") });
            var psi = GitService.CreateStartInfo(neutral, PullRequestService.CredentialFillArgs, true, env);

            Assert.Equal(neutral, psi.WorkingDirectory);
            Assert.Empty(Directory.EnumerateFileSystemEntries(neutral));
            foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_CONFIG", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT" })
                Assert.False(psi.Environment.ContainsKey(name), name);
            Assert.Equal(Path.GetDirectoryName(neutral), psi.Environment["GIT_CEILING_DIRECTORIES"]);
            Assert.Equal("never", psi.Environment["GCM_INTERACTIVE"]);
            Assert.Equal(GitService.SafeConfig.Concat(new[] { "-c", "credential.interactive=false", "credential", "fill" }), psi.ArgumentList);
        }
        finally
        {
            Directory.Delete(neutral, true);
        }
    }

    [Fact]
    public async Task Git_run_outside_a_repository_sees_none()
    {
        repo.Git("config", "credential.helper", "!" + Touch(Path.Combine(outside, "helper-ran")));

        var r = await GitService.RunOutsideRepositoryAsync(["rev-parse", "--git-dir"]);
        var helper = await GitService.RunOutsideRepositoryAsync(["config", "--local", "--get", "credential.helper"]);

        Assert.False(r.Ok);
        Assert.Contains("not a git repository", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(helper.Ok);
    }

    [Theory]
    [InlineData("https://dev.azure.com/org%0Ahost=evil.example/project/_git/repo")]
    [InlineData("https://dev.azure.com/org/project%0D%0Apassword=x/_git/repo")]
    [InlineData("https://dev.azure.com/org/project/_git/repo%0Aprotocol=http")]
    [InlineData("https://dev.azure.com/org/project/_git/repo%00")]
    [InlineData("https://org.visualstudio.com/proj%09ect/_git/repo")]
    [InlineData("git@ssh.dev.azure.com:v3/org/project/re%0Apo")]
    public void Azure_names_with_control_characters_are_refused(string url) => Assert.Null(PullRequestService.ParseRemote(url));

    [Fact]
    public void A_host_with_a_line_break_gets_no_credential_request()
    {
        Assert.Null(PullRequestService.CredentialInput(new RepoHost("azure", "dev.azure.com", "org", "proj\nhost=evil.example", "repo")));
        Assert.Equal("protocol=https\nhost=dev.azure.com\npath=org/proj/_git/repo\n\n",
            PullRequestService.CredentialInput(new RepoHost("azure", "dev.azure.com", "org", "proj", "repo")));
    }

    // ---- New files described for the commit message

    [Fact]
    public async Task An_untracked_link_to_a_file_outside_isnt_read()
    {
        repo.Commit(("a.txt", "one\n"));
        var secret = Path.Combine(outside, "id_rsa");
        File.WriteAllText(secret, "PRIVATE KEY MATERIAL\n");
        try { File.CreateSymbolicLink(Path.Combine(repo.Root, "notes.txt"), secret); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // Windows without the right to make links
        repo.Write("plain.txt", "ordinary new file\n");

        var prompt = await CommitMessageService.BuildPromptAsync(repo.Root);

        Assert.NotNull(prompt);
        Assert.Contains("ordinary new file", prompt);
        Assert.DoesNotContain("PRIVATE KEY MATERIAL", prompt);
        Assert.Equal("", CommitMessageService.ReadStart(repo.Root, "notes.txt"));
    }

    [WindowsFact]
    public async Task A_file_through_a_linked_folder_isnt_read()
    {
        repo.Commit(("a.txt", "one\n"));
        File.WriteAllText(Path.Combine(outside, "id_rsa"), "PRIVATE KEY MATERIAL\n");
        FileSystemFixtures.Junction(Path.Combine(repo.Root, "linked"), outside);   // no elevation needed, unlike a symbolic link
        repo.Write("plain.txt", "ordinary\n");

        var prompt = await CommitMessageService.BuildPromptAsync(repo.Root);

        Assert.Contains("ordinary", prompt);
        Assert.DoesNotContain("PRIVATE KEY MATERIAL", prompt);
        Assert.Equal("", CommitMessageService.ReadStart(repo.Root, "linked/id_rsa"));
        Assert.Equal("", CommitMessageService.ReadStart(repo.Root, "../" + Path.GetFileName(outside) + "/id_rsa"));
        Assert.Equal("ordinary\n\n", CommitMessageService.ReadStart(repo.Root, "plain.txt"));
    }
}
