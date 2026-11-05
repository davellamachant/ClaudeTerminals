using LlamaTree.Git;

namespace LlamaTree.Tests;

/// <summary>
/// Security finding H3, what was left: a repository's own settings that can run programs (filter drivers, ssh command,
/// credential helper, hooks...) don't apply to the app's git calls until the person trusts the repository. Against real
/// git, with the person's global config replaced by an empty file (GIT_CONFIG_GLOBAL) so theirs can't interfere.
/// In the non-parallel collection because it sets that variable for the whole process.
/// </summary>
[Collection(nameof(GitUndoTests))]
public sealed class RepoTrustTests : IDisposable
{
    private readonly string outside = Path.Combine(Path.GetTempPath(), "ct-trust-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string globalConfig;
    private readonly string? previousGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
    private readonly RepoTrustStore previousStore = RepoTrust.Store;
    private readonly TempRepo repo;

    public RepoTrustTests()
    {
        Directory.CreateDirectory(outside);
        globalConfig = Path.Combine(outside, "global.gitconfig");
        File.WriteAllText(globalConfig, "");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", globalConfig);
        RepoTrust.Store = new RepoTrustStore(null);
        RepoTrust.ClearCache();
        repo = new TempRepo();
    }

    public void Dispose()
    {
        repo.Dispose();
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousGlobal);
        RepoTrust.Store = previousStore;
        RepoTrust.ClearCache();
        try { FileSystemFixtures.DeleteTree(outside); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A shell command (git runs these through sh, on Windows too) that leaves <paramref name="marker"/> behind.</summary>
    private static string Touch(string marker) => $"echo ran > '{marker.Replace('\\', '/')}'";

    private int stamp;

    /// <summary>Gives the file a new time but the same size and content, so the next status has to read it again (through the clean filter).</summary>
    private void Retouch(string path) => File.SetLastWriteTimeUtc(Path.Combine(repo.Root, path), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(++stamp));

    /// <summary>A committed a.dat that .gitattributes sends through the "trap" filter, whose clean leaves a marker; set after the commit.</summary>
    private string FilterTrap(string kind = "clean")
    {
        repo.Commit((".gitattributes", "*.dat filter=trap\n"), ("a.dat", "hello\n"));
        var marker = Path.Combine(outside, kind + "-ran");
        repo.Git("config", $"filter.trap.{kind}", kind == "process" ? Touch(marker) : Touch(marker) + "; cat");
        if (kind == "process") repo.Git("config", "filter.trap.required", "true");
        // The trap works: plain git runs it.
        Retouch("a.dat");
        try { repo.Git("status", "--porcelain"); } catch (InvalidOperationException) { /* a process filter that isn't one fails status */ }
        Assert.True(File.Exists(marker), $"git didn't run the {kind} filter at all, so this test proves nothing");
        File.Delete(marker);
        return marker;
    }

    // ---- Automatic status

    [Theory]
    [InlineData("clean")]
    [InlineData("process")]
    public async Task Status_doesnt_run_the_repositorys_filter_until_its_trusted(string kind)
    {
        var marker = FilterTrap(kind);

        Retouch("a.dat");
        var status = await GitService.GetStatusAsync(repo.Root);
        Retouch("a.dat");
        var overview = await GitService.GetWorktreeOverviewAsync(repo.Root);

        Assert.True(status.IsRepo, status.Error);
        Assert.Single(overview.Worktrees);
        Assert.False(File.Exists(marker), "the app's automatic git status ran the repository's filter");
        Assert.NotNull(status.Trust);
        Assert.False(status.Trust.Trusted);
        Assert.Contains(status.Trust.Keys, k => k.Key == $"filter.trap.{kind}" && k.Where == "local");

        Assert.True(await RepoTrust.SetAsync(repo.Root, true, status.Trust.Hash));
        Retouch("a.dat");
        await GitService.GetStatusAsync(repo.Root);   // a process filter that isn't one makes it fail, once it runs

        Assert.True((await RepoTrust.DescribeAsync(repo.Root))!.Trusted);
        Assert.True(File.Exists(marker), "once trusted, the repository's filter applies again");
    }

    [Fact]
    public async Task A_filter_from_a_file_the_config_includes_is_turned_off_too()
    {
        repo.Commit((".gitattributes", "*.dat filter=trap\n"), ("a.dat", "hello\n"));
        var marker = Path.Combine(outside, "included-ran");
        var included = Path.Combine(outside, "extra.gitconfig");
        File.WriteAllText(included, $"[filter \"trap\"]\n\tclean = \"{Touch(marker).Replace("\"", "\\\"")}; cat\"\n");
        repo.Git("config", "include.path", included.Replace('\\', '/'));

        Retouch("a.dat");
        var status = await GitService.GetStatusAsync(repo.Root);

        Assert.False(File.Exists(marker), "status ran a filter from an included file");
        Assert.Contains(status.Trust!.Keys, k => k.Key == "filter.trap.clean");
        Assert.Contains(status.Trust.Keys, k => k.Key == "include.path");   // settings from outside the repository are named
    }

    [Fact]
    public async Task Discovery_is_read_again_only_when_a_config_file_changes()
    {
        var first = await RepoTrust.GetSettingsAsync(repo.Root);
        var again = await RepoTrust.GetSettingsAsync(repo.Root);
        Assert.Same(first, again);

        repo.Git("config", "core.sshCommand", "ssh -i somewhere");
        var changed = await RepoTrust.GetSettingsAsync(repo.Root);

        Assert.NotSame(first, changed);
        Assert.Contains(changed!.Keys, k => k.Key == "core.sshcommand");
    }

    // ---- Commands the person starts

    [Fact]
    public async Task Fetch_is_refused_while_untrusted_and_runs_once_trusted()
    {
        repo.Commit(("a.txt", "one\n"));
        var ssh = Path.Combine(outside, "ssh-ran");
        var helper = Path.Combine(outside, "helper-ran");
        repo.Git("remote", "add", "origin", "ssh://git@example.invalid/x.git");
        repo.Git("config", "core.sshCommand", Touch(ssh) + "; false");
        repo.Git("config", "credential.helper", "!" + Touch(helper));

        var fetch = await GitService.FetchAsync(repo.Root);
        var pull = await GitService.PullAsync(repo.Root);
        var push = await GitService.PushAsync(repo.Root);
        repo.Write("a.txt", "two\n");
        var commit = await GitService.CommitAsync(repo.Root, "msg", all: true);
        var stage = await GitService.StageAsync(repo.Root, ["a.txt"]);

        foreach (var r in new[] { fetch, pull, push, commit, stage })
        {
            Assert.False(r.Ok);
            Assert.True(r.NeedsTrust);
            Assert.Contains("core.sshcommand", r.Error);
        }
        Assert.Contains("credential.helper", fetch.Error);
        Assert.False(File.Exists(ssh), "fetch ran the repository's core.sshCommand");
        Assert.False(File.Exists(helper));
        Assert.Contains("M a.txt", repo.Git("status", "--porcelain"));   // nothing was committed or staged
        await Assert.ThrowsAsync<RepoTrustRequiredException>(() =>
            GitService.UndoConfirmedAsync(repo.Root, false, [new FileChange("a.txt", null, "M", InHead: true)]));
        Assert.True((await GitService.CheckoutBranchAsync(repo.Root, "main", false, false)).NeedsTrust);

        var settings = await RepoTrust.GetSettingsAsync(repo.Root);
        Assert.True(await RepoTrust.SetAsync(repo.Root, true, settings!.Hash));
        var trusted = await GitService.FetchAsync(repo.Root);

        Assert.False(trusted.NeedsTrust);
        Assert.True(File.Exists(ssh), "once trusted, fetch uses the repository's own ssh command");
    }

    [Fact]
    public async Task A_changed_risky_setting_asks_again()
    {
        repo.Git("config", "credential.helper", "!echo one");
        var before = await RepoTrust.DescribeAsync(repo.Root);
        Assert.True(await RepoTrust.SetAsync(repo.Root, true, before!.Hash));
        Assert.True((await RepoTrust.DescribeAsync(repo.Root))!.Trusted);

        repo.Git("config", "credential.helper", "!echo something-else");
        var after = await RepoTrust.DescribeAsync(repo.Root);

        Assert.False(after!.Trusted);
        Assert.NotEqual(before.Hash, after.Hash);
        Assert.NotNull(await RepoTrust.RefusalAsync(repo.Root, "fetch"));
        // Trusting with the hash the person saw before the change does nothing.
        Assert.False(await RepoTrust.SetAsync(repo.Root, true, before.Hash));
        Assert.False((await RepoTrust.DescribeAsync(repo.Root))!.Trusted);

        // Adding another risky setting asks again too; taking trust back asks again.
        Assert.True(await RepoTrust.SetAsync(repo.Root, true, after.Hash));
        repo.Git("config", "core.hooksPath", "hooks-elsewhere");
        Assert.False((await RepoTrust.DescribeAsync(repo.Root))!.Trusted);
        var now = await RepoTrust.DescribeAsync(repo.Root);
        Assert.True(await RepoTrust.SetAsync(repo.Root, true, now!.Hash));
        Assert.True(await RepoTrust.SetAsync(repo.Root, false, null));
        Assert.False((await RepoTrust.DescribeAsync(repo.Root))!.Trusted);
    }

    [Fact]
    public async Task Trust_is_shared_by_every_worktree_of_the_repository()
    {
        repo.Commit(("a.txt", "one\n"));
        var linked = repo.Root + "-wt";
        repo.Git("worktree", "add", "-q", "-b", "side", linked);
        try
        {
            repo.Git("config", "core.sshCommand", "evil");
            var main = await RepoTrust.DescribeAsync(repo.Root);
            Assert.True(await RepoTrust.SetAsync(linked, true, main!.Hash));
            Assert.True((await RepoTrust.DescribeAsync(repo.Root))!.Trusted);
            Assert.Equal(main.Repo, (await RepoTrust.DescribeAsync(linked))!.Repo);
        }
        finally
        {
            try { FileSystemFixtures.DeleteTree(linked); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---- Unaffected

    [Fact]
    public async Task A_repository_without_risky_settings_is_unaffected()
    {
        repo.Commit(("a.txt", "one\n"));
        repo.Git("config", "credential.helper", "");          // an empty helper only clears the list
        repo.Git("config", "core.fsmonitor", "false");
        repo.Git("config", "protocol.ext.allow", "never");
        repo.Git("config", "remote.origin.url", "https://example.invalid/x.git");
        repo.Write("a.txt", "two\n");

        var status = await GitService.GetStatusAsync(repo.Root);

        Assert.Null(status.Trust);
        Assert.Null(await RepoTrust.DescribeAsync(repo.Root));
        Assert.Empty(await RepoTrust.OverridesForAsync(repo.Root));
        Assert.Null(await RepoTrust.RefusalAsync(repo.Root, "fetch"));
        Assert.False((await GitService.CommitAsync(repo.Root, "msg", all: true)).NeedsTrust);
        Assert.Contains("msg", repo.Git("log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task Discovery_ignores_the_persons_global_config()
    {
        File.WriteAllText(globalConfig,
            "[credential]\n\thelper = !echo global\n[core]\n\tsshCommand = ssh -v\n[filter \"lfs\"]\n\tclean = git-lfs clean -- %f\n\tprocess = git-lfs filter-process\n");
        RepoTrust.ClearCache();
        Assert.Contains("ssh -v", repo.Git("config", "core.sshCommand"));   // git does read it

        Assert.Null(await RepoTrust.DescribeAsync(repo.Root));
        Assert.Empty(await RepoTrust.OverridesForAsync(repo.Root));
    }

    [Fact]
    public async Task Not_a_repository_has_no_settings()
    {
        Assert.Null(await RepoTrust.GetSettingsAsync(outside));
        Assert.Null(await RepoTrust.GetSettingsAsync(Path.Combine(outside, "missing")));
    }

    // ---- Other places a repository keeps settings

    [Fact]
    public async Task Hooks_a_submodules_config_and_a_worktree_config_are_found()
    {
        repo.Write(".git/hooks/pre-commit", "#!/bin/sh\nexit 0\n");
        repo.Write(".git/modules/libs/sub/config", "[core]\n\tbare = false\n[filter \"x=y\"]\n\tsmudge = evil\n");
        repo.Git("config", "extensions.worktreeConfig", "true");
        repo.Git("config", "--worktree", "core.editor", "evil-editor");

        var s = await RepoTrust.GetSettingsAsync(repo.Root);

        Assert.Contains(s!.Keys, k => k.Key == "pre-commit" && k.Where == "hook");
        Assert.Contains(s.Keys, k => k.Key == "filter.x=y.smudge" && k.Where == "submodule libs/sub");
        Assert.Contains(s.Keys, k => k.Key == "core.editor" && k.Where == "worktree");
        Assert.DoesNotContain(s.Keys, k => k.Key.EndsWith(".sample"));
        // The submodule's driver is turned off too, its name kept whole although it holds an "=".
        Assert.Contains(("filter.x=y.smudge", ""), s.Overrides);
        Assert.Contains(("filter.x=y.required", "false"), s.Overrides);

        // A hook whose content changes asks again.
        Assert.True(await RepoTrust.SetAsync(repo.Root, true, s.Hash));
        repo.Write(".git/hooks/pre-commit", "#!/bin/sh\necho changed; exit 0\n");
        Assert.False((await RepoTrust.DescribeAsync(repo.Root))!.Trusted);
    }

    // ---- The parts

    [Theory]
    [InlineData("filter.lfs.clean", "git-lfs clean -- %f", true)]
    [InlineData("filter.a.b.process", "x", true)]
    [InlineData("filter.lfs.required", "true", false)]
    [InlineData("diff.pdf.textconv", "pdftotext", true)]
    [InlineData("diff.x.command", "x", true)]
    [InlineData("diff.external", "x", true)]
    [InlineData("diff.renames", "true", false)]
    [InlineData("merge.ours.driver", "true", true)]
    [InlineData("core.sshcommand", "ssh", true)]
    [InlineData("core.askpass", "x", true)]
    [InlineData("core.editor", "vim", true)]
    [InlineData("core.pager", "less", true)]
    [InlineData("core.hookspath", ".husky", true)]
    [InlineData("core.gitproxy", "x", true)]
    [InlineData("core.fsmonitor", "true", true)]
    [InlineData("core.fsmonitor", "false", false)]
    [InlineData("core.autocrlf", "true", false)]
    [InlineData("credential.helper", "!evil", true)]
    [InlineData("credential.helper", "", false)]
    [InlineData("credential.https://example.com.helper", "store", true)]
    [InlineData("credential.usehttppath", "true", false)]
    [InlineData("gpg.program", "gpg2", true)]
    [InlineData("gpg.ssh.program", "ssh-keygen", true)]
    [InlineData("gpg.ssh.defaultkeycommand", "x", true)]
    [InlineData("gpg.format", "ssh", false)]
    [InlineData("remote.origin.uploadpack", "x", true)]
    [InlineData("remote.origin.receivepack", "x", true)]
    [InlineData("remote.origin.vcs", "x", true)]
    [InlineData("remote.origin.url", "https://example.com/x.git", false)]
    [InlineData("url.ext::sh -c evil.insteadof", "https://github.com/", true)]
    [InlineData("url./tmp/local.insteadof", "https://github.com/", true)]
    [InlineData("url.C:/repos/x.pushinsteadof", "https://github.com/", true)]
    [InlineData("url.https://mirror.example.com/.insteadof", "https://github.com/", false)]
    [InlineData("url.git@github.com:.insteadof", "https://github.com/", false)]
    [InlineData("protocol.allow", "always", true)]
    [InlineData("protocol.ext.allow", "always", true)]
    [InlineData("protocol.ext.allow", "never", false)]
    [InlineData("protocol.version", "2", false)]
    [InlineData("sequence.editor", "x", true)]
    [InlineData("uploadpack.packobjectshook", "x", true)]
    [InlineData("submodule.sub.update", "!evil", true)]
    [InlineData("submodule.sub.update", "rebase", false)]
    [InlineData("user.name", "x", false)]
    public void Risky_settings(string key, string value, bool risky) => Assert.Equal(risky, RepoTrust.IsRisky(key, value));

    [Fact]
    public void Config_listing_is_parsed_with_bare_keys_and_values_with_newlines()
    {
        var text = "file:C:/r/.git/config\0core.bare\nfalse\0file:C:/r/.git/config\0novalue.flag\0file:C:/x\0a.b\nline1\nline2\0";
        var list = RepoTrust.ParseList(text).ToList();
        Assert.Equal([("C:/r/.git/config", "core.bare", "false"), ("C:/r/.git/config", "novalue.flag", null), ("C:/x", "a.b", "line1\nline2")], list);
        Assert.Equal(("filter", "a.b", "clean"), RepoTrust.Split("filter.a.b.clean"));
        Assert.Equal(("core", null, "sshcommand"), RepoTrust.Split("Core.SshCommand"));
    }

    [Fact]
    public void Overrides_go_after_any_the_environment_already_adds()
    {
        var env = GitService.WithOverrides(new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = "1", ["X"] = "y" },
            [("filter.a=b.clean", ""), ("filter.a=b.required", "false")])!;
        Assert.Equal("3", env["GIT_CONFIG_COUNT"]);
        Assert.Equal("filter.a=b.clean", env["GIT_CONFIG_KEY_1"]);
        Assert.Equal("", env["GIT_CONFIG_VALUE_1"]);
        Assert.Equal("false", env["GIT_CONFIG_VALUE_2"]);
        Assert.Equal("y", env["X"]);
        Assert.Null(GitService.WithOverrides(null, []));
    }

    [Fact]
    public void Trust_is_kept_in_its_file()
    {
        var file = Path.Combine(outside, "repo-trust.json");
        new RepoTrustStore(file).Trust(@"C:\Repos\X\.git", "abc");

        var reloaded = new RepoTrustStore(file);
        Assert.True(reloaded.IsTrusted(OperatingSystem.IsWindows() ? @"c:\repos\x\.git\" : @"C:\Repos\X\.git", "abc"));
        Assert.False(reloaded.IsTrusted(@"C:\Repos\X\.git", "other"));
        reloaded.Revoke(@"C:\Repos\X\.git");
        Assert.False(new RepoTrustStore(file).IsTrusted(@"C:\Repos\X\.git", "abc"));

        File.WriteAllText(file, "{ not json");
        Assert.False(new RepoTrustStore(file).IsTrusted(@"C:\Repos\X\.git", "abc"));
    }
}
