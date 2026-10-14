using ClaudeTerminals.Git;

namespace ClaudeTerminals.Tests;

[CollectionDefinition(nameof(GitUndoTests), DisableParallelization = true)]
public sealed class GitUndoCollection;

/// <summary>
/// Undo Changes against real repositories (review findings F4, F5). The Recycle Bin is replaced by a recorder so the
/// tests can see what would have been recycled without filling the real one.
/// </summary>
[Collection(nameof(GitUndoTests))]
public sealed class GitUndoTests : IDisposable
{
    private readonly TempRepo repo = new();
    private readonly Action<string> realRecycle = GitService.SendToRecycleBin;
    private readonly Func<string, IEnumerable<string>, IReadOnlyCollection<string>, Task<GitResult>> realRun = GitService.RunWithPaths;
    private readonly List<string> recycled = [];

    public GitUndoTests()
    {
        GitService.SendToRecycleBin = full =>
        {
            recycled.Add(Path.GetRelativePath(repo.Root, full).Replace('\\', '/'));
            File.Delete(full);
        };
    }

    public void Dispose()
    {
        GitService.SendToRecycleBin = realRecycle;
        GitService.RunWithPaths = realRun;
        repo.Dispose();
    }

    /// <summary>Makes the path-list git command whose verb is <paramref name="verb"/> fail in the given way; others run for real.</summary>
    private void FailStep(string verb, Func<Task<GitResult>> failure) =>
        GitService.RunWithPaths = (dir, args, paths) => args.First() == verb ? failure() : realRun(dir, args, paths);

    /// <summary>What the confirmation dialog shows and sends back: the files, the commit, and which have unstaged edits too.</summary>
    private sealed record Confirmation(bool Staged, List<FileChange> Files, string? Head, List<string> AlsoUnstaged);

    private async Task<Confirmation> Confirm(bool staged, Func<FileChange, bool>? select = null)
    {
        var st = await GitService.GetStatusAsync(repo.Root);
        var files = (staged ? st.Staged : st.Unstaged).Where(select ?? (_ => true)).ToList();
        var unstaged = st.Unstaged.Select(f => f.Path).ToHashSet();
        var alsoUnstaged = staged ? files.Where(f => unstaged.Contains(f.Path)).Select(f => f.Path).ToList() : [];
        return new Confirmation(staged, files, st.HeadId, alsoUnstaged);   // as the page sends it
    }

    private Task<int> Undo(Confirmation c) => GitService.UndoConfirmedAsync(repo.Root, c.Staged, c.Files, c.Head, c.AlsoUnstaged);

    // ---- F4: a staged rename must never overwrite or delete a file at the original name

    [Theory]
    [InlineData("new content")]
    [InlineData("old")]   // same bytes as the committed file: still somebody's file, still not ours to overwrite
    public async Task Staged_rename_with_a_file_back_at_the_original_name_is_refused(string content)
    {
        repo.Commit(("a.txt", "old"));
        repo.Git("mv", "a.txt", "b.txt");
        repo.Write("a.txt", content);
        var confirmed = await Confirm(true);
        var before = repo.Snapshot();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal(content, repo.Read("a.txt"));
        Assert.True(repo.Exists("b.txt"));
        Assert.Empty(recycled);
        Assert.Equal(before, repo.Snapshot());
    }

    [Fact]
    public async Task Staged_rename_original_recreated_after_confirming_is_refused()
    {
        repo.Commit(("a.txt", "old"));
        repo.Git("mv", "a.txt", "b.txt");
        var confirmed = await Confirm(true);   // dialog shown while a.txt does not exist
        repo.Write("a.txt", "created while the dialog was open");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal("created while the dialog was open", repo.Read("a.txt"));
        Assert.True(repo.Exists("b.txt"));
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task Staged_rename_with_nothing_at_the_original_name_is_undone()
    {
        repo.Commit(("a.txt", "old"));
        repo.Git("mv", "a.txt", "b.txt");

        Assert.Equal(1, await Undo(await Confirm(true)));

        Assert.Equal("old", repo.Read("a.txt"));
        Assert.False(repo.Exists("b.txt"));
        Assert.Equal(new[] { "b.txt" }, recycled);
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Case_only_rename_is_undone_without_recycling_the_file()
    {
        repo.Commit(("a.txt", "keep me"));
        repo.Git("mv", "a.txt", "A.txt");

        await Undo(await Confirm(true));

        Assert.Equal("keep me", repo.Read("a.txt"));
        Assert.Empty(recycled);
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Staged_deletion_with_a_file_back_on_disk_is_refused()
    {
        repo.Commit(("d.txt", "old"));
        repo.Git("rm", "--cached", "-q", "d.txt");
        repo.Write("d.txt", "mine");
        var confirmed = await Confirm(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal("mine", repo.Read("d.txt"));
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task Staged_copy_undo_removes_the_copy_and_leaves_its_source_alone()
    {
        repo.Git("config", "status.renames", "copies");
        repo.Commit(("src.txt", "line 1\nline 2\nline 3\nline 4\nline 5\n"));
        repo.Write("copy.txt", "line 1\nline 2\nline 3\nline 4\nline 5\n");
        repo.Write("src.txt", "line 1\nline 2\nline 3\nline 4\nline 5\nstaged edit\n");
        repo.Git("add", "-A");
        var confirmed = await Confirm(true, f => f.Path == "copy.txt");
        Assert.Equal("src.txt", Assert.Single(confirmed.Files).OrigPath);   // git reported a copy

        Assert.Equal(1, await Undo(confirmed));

        Assert.Equal(new[] { "copy.txt" }, recycled);
        Assert.Contains("staged edit", repo.Read("src.txt"));
        Assert.Contains("src.txt", repo.Git("diff", "--cached", "--name-only"));   // the source's staged edit is still staged
    }

    // ---- F4: the undo performed must be the one confirmed

    [Fact]
    public async Task Unstaged_edit_that_became_untracked_after_confirming_is_not_deleted()
    {
        repo.Commit(("x.txt", "v1"));
        repo.Write("x.txt", "v2");
        var confirmed = await Confirm(false);   // "restore x.txt to the staged version"
        repo.Git("rm", "--cached", "-q", "x.txt");   // now untracked: the same click would delete it

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal("v2", repo.Read("x.txt"));
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task Staged_change_whose_state_changed_after_confirming_is_refused()
    {
        repo.Commit(("x.txt", "v1"));
        repo.Write("x.txt", "v2");
        repo.Git("add", "x.txt");
        var confirmed = await Confirm(true);
        repo.Git("rm", "--cached", "-q", "x.txt");
        var before = repo.Snapshot();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal("v2", repo.Read("x.txt"));
        Assert.Equal(before, repo.Snapshot());
    }

    [Fact]
    public async Task Staged_change_that_gained_unstaged_edits_after_confirming_is_refused()
    {
        repo.Commit(("x.txt", "v1"));
        repo.Write("x.txt", "v2");
        repo.Git("add", "x.txt");
        var confirmed = await Confirm(true);   // the dialog did not warn about unstaged edits
        repo.Write("x.txt", "v3 typed while the dialog was open");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal("v3 typed while the dialog was open", repo.Read("x.txt"));
    }

    [Fact]
    public async Task Staged_change_whose_unstaged_edits_were_warned_about_is_undone()
    {
        repo.Commit(("x.txt", "v1"));
        repo.Write("x.txt", "v2");
        repo.Git("add", "x.txt");
        repo.Write("x.txt", "v3");
        var confirmed = await Confirm(true);
        Assert.Equal(new[] { "x.txt" }, confirmed.AlsoUnstaged);

        Assert.Equal(1, await Undo(confirmed));

        Assert.Equal("v1", repo.Read("x.txt"));
    }

    [Fact]
    public async Task Staged_change_when_the_last_commit_moved_after_confirming_is_refused()
    {
        repo.Commit(("x.txt", "v1"), ("other.txt", "o1"));
        repo.Write("x.txt", "v2");
        repo.Git("add", "x.txt");
        var confirmed = await Confirm(true);
        repo.Write("other.txt", "o2");
        repo.Git("commit", "-q", "-m", "meanwhile", "--", "other.txt");   // x.txt's entry is unchanged; HEAD is not

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal("v2", repo.Read("x.txt"));
    }

    [Fact]
    public async Task Staged_addition_committed_after_confirming_is_not_deleted()
    {
        repo.Commit(("base.txt", "base"));
        repo.Write("new.txt", "new");
        repo.Git("add", "new.txt");
        var confirmed = await Confirm(true);
        repo.Git("commit", "-q", "-m", "committed meanwhile");

        Assert.Equal(0, await Undo(confirmed));

        Assert.Equal("new", repo.Read("new.txt"));
        Assert.Empty(recycled);
    }

    // ---- F5: in-HEAD classification, failures, unborn branch

    [Fact]
    public async Task Mixed_selection_restores_tracked_files_and_recycles_added_ones()
    {
        repo.Commit(("t.txt", "v1"));
        repo.Write("t.txt", "v2");
        repo.Write("n.txt", "added");
        repo.Git("add", "-A");

        Assert.Equal(2, await Undo(await Confirm(true)));

        Assert.Equal("v1", repo.Read("t.txt"));
        Assert.Equal(new[] { "n.txt" }, recycled);
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Staged_deletion_is_restored_from_HEAD()
    {
        repo.Commit(("d.txt", "content"));
        repo.Git("rm", "-q", "d.txt");

        await Undo(await Confirm(true));

        Assert.Equal("content", repo.Read("d.txt"));
        Assert.Empty(recycled);
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Unborn_branch_staged_additions_are_recycled()
    {
        repo.Write("n.txt", "first");
        repo.Git("add", "n.txt");

        Assert.Equal(1, await Undo(await Confirm(true)));

        Assert.Equal(new[] { "n.txt" }, recycled);
        Assert.Equal("", repo.Git("ls-files"));
    }

    [Fact]
    public async Task Broken_HEAD_is_not_mistaken_for_an_unborn_branch()
    {
        repo.Commit(("t.txt", "v1"));
        repo.Write("t.txt", "v2");
        repo.Write("n.txt", "added");
        repo.Git("add", "-A");
        var confirmed = await Confirm(true);
        // HEAD now names a commit that doesn't exist: lookups of HEAD fail.
        File.WriteAllText(Path.Combine(repo.Root, ".git", "refs", "heads", "main"), "0123456789012345678901234567890123456789\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal("v2", repo.Read("t.txt"));
        Assert.Equal("added", repo.Read("n.txt"));
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task Restore_without_a_known_commit_changes_nothing()
    {
        repo.Commit(("t.txt", "v1"));
        repo.Write("t.txt", "v2");
        repo.Write("n.txt", "added");
        repo.Git("add", "-A");
        var items = (await GitService.GetStatusAsync(repo.Root)).Staged;
        var before = repo.Snapshot();

        var result = await GitService.UndoAsync(repo.Root, true, items, headOid: null);

        Assert.False(result.Ok);
        Assert.Equal(before, repo.Snapshot());
        Assert.Empty(recycled);
    }

    [Theory]
    [InlineData("-dash.txt")]
    [InlineData("spa ce.txt")]
    [InlineData("ünïcödé.txt")]
    [InlineData("[x] glob.txt")]
    public async Task Unusual_file_names_are_restored(string name)
    {
        repo.Commit((name, "v1"));
        repo.Write(name, "v2");
        repo.Git("add", "-A");

        await Undo(await Confirm(true));

        Assert.Equal("v1", repo.Read(name));
        Assert.Empty(recycled);
    }

    // ==== Follow-up: second review (C10 and the small items) ====

    // ---- Confirmation binding

    [Fact]
    public async Task Staged_change_whose_warned_unstaged_edits_went_away_after_confirming_is_refused()
    {
        repo.Commit(("x.txt", "v1"));
        repo.Write("x.txt", "v2");
        repo.Git("add", "x.txt");
        repo.Write("x.txt", "v3");
        var confirmed = await Confirm(true);   // warned: x.txt also has unstaged edits
        repo.Git("add", "x.txt");              // now staged as v3, nothing unstaged
        var before = repo.Snapshot();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal(before, repo.Snapshot());
    }

    [Fact]
    public async Task One_stale_file_refuses_the_whole_selection_before_touching_any()
    {
        repo.Commit(("a.txt", "a1"), ("b.txt", "b1"));
        repo.Write("a.txt", "a2");
        repo.Write("b.txt", "b2");
        repo.Git("add", "-A");
        var confirmed = await Confirm(true);
        repo.Write("b.txt", "b3 typed while the dialog was open");   // only b.txt goes stale
        var before = repo.Snapshot();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal(before, repo.Snapshot());   // a.txt, which still matched, was not undone either
    }

    [Fact]
    public async Task Confirmed_on_an_unborn_branch_but_committed_by_the_time_of_Undo_is_refused()
    {
        repo.Write("n.txt", "n");
        repo.Write("m.txt", "m");
        repo.Git("add", "-A");
        var confirmed = await Confirm(true, f => f.Path == "n.txt");
        Assert.Equal("(initial)", confirmed.Head);
        repo.Git("commit", "-q", "-m", "first", "--", "m.txt");   // n.txt is still a staged addition; HEAD now exists
        var before = repo.Snapshot();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal(before, repo.Snapshot());
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task Confirmed_at_a_commit_but_unborn_by_the_time_of_Undo_is_refused()
    {
        repo.Commit(("t.txt", "v1"));
        repo.Write("t.txt", "v2");
        repo.Git("add", "t.txt");
        var confirmed = await Confirm(true);
        repo.Git("checkout", "-q", "--orphan", "other");   // a branch with no commits; the index is kept
        var before = repo.Snapshot();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal(before, repo.Snapshot());
        Assert.Empty(recycled);
    }

    // ---- Case-only renames are decided by file identity, not spelling

    private string CaseSensitiveFolder()
    {
        FileSystemFixtures.MakeCaseSensitive(Path.Combine(repo.Root, "cs"));
        repo.Git("config", "core.ignorecase", "false");
        return "cs";
    }

    [Theory]
    [InlineData("someone else's file")]
    [InlineData("same")]   // identical bytes are still a different file
    public async Task Case_only_rename_in_a_case_sensitive_folder_with_a_file_at_the_old_name_is_refused(string content)
    {
        var cs = CaseSensitiveFolder();
        repo.Commit(($"{cs}/a.txt", "same"));
        repo.Git("mv", $"{cs}/a.txt", $"{cs}/A.txt");
        repo.Write($"{cs}/a.txt", content);
        var confirmed = await Confirm(true);
        Assert.Equal("R", Assert.Single(confirmed.Files).Status);
        var before = repo.Snapshot();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Equal(content, repo.Read($"{cs}/a.txt"));
        Assert.Equal("same", repo.Read($"{cs}/A.txt"));
        Assert.Equal(before, repo.Snapshot());
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task Case_only_rename_in_a_case_sensitive_folder_restores_the_old_name_and_recycles_the_new_one()
    {
        var cs = CaseSensitiveFolder();
        repo.Commit(($"{cs}/a.txt", "orig"));
        repo.Git("mv", $"{cs}/a.txt", $"{cs}/A.txt");

        Assert.Equal(1, await Undo(await Confirm(true)));

        Assert.Equal("orig", repo.Read($"{cs}/a.txt"));
        Assert.Equal(new[] { $"{cs}/A.txt" }, recycled);   // the distinct new-name file, never the restored one
        Assert.Equal(new[] { "a.txt" }, Directory.GetFiles(Path.Combine(repo.Root, cs)).Select(Path.GetFileName));
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    // ---- The Recycle Bin step refuses anything that really lives outside the repository

    [Fact]
    public async Task Recycling_refuses_a_file_reached_through_a_junction_wherever_it_is_in_the_list()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ct-out-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "keep");
        try
        {
            FileSystemFixtures.Junction(Path.Combine(repo.Root, "link"), outside);
            repo.Write("first.txt", "1");
            repo.Write("last.txt", "2");
            var items = new[] { "first.txt", "link/secret.txt", "last.txt" }.Select(p => new FileChange(p, null, "A", Untracked: true)).ToList();

            var result = await GitService.UndoAsync(repo.Root, false, items);

            Assert.False(result.Ok);
            Assert.Contains("link/secret.txt", result.Error);
            Assert.Contains("only partly applied", result.Error);   // the two safe files were recycled
            Assert.Equal(new[] { "first.txt", "last.txt" }, recycled);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "secret.txt")));
        }
        finally
        {
            if (Directory.Exists(Path.Combine(repo.Root, "link"))) Directory.Delete(Path.Combine(repo.Root, "link"));
            FileSystemFixtures.DeleteTree(outside);
        }
    }

    [Fact]
    public async Task Recycling_refuses_a_differently_cased_sibling_reached_through_a_junction()
    {
        var top = Path.Combine(Path.GetTempPath(), "ct-casepar-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            FileSystemFixtures.MakeCaseSensitive(top);
            var root = Path.Combine(top, "Repo");
            var sibling = Path.Combine(top, "repo");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "secret.txt"), "keep");
            FileSystemFixtures.Junction(Path.Combine(root, "link"), sibling);

            var result = await GitService.UndoAsync(root, false, [new FileChange("link/secret.txt", null, "A", Untracked: true)]);

            Assert.False(result.Ok);
            Assert.DoesNotContain("partly", result.Error);   // nothing else was done
            Assert.Empty(recycled);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(sibling, "secret.txt")));
        }
        finally
        {
            FileSystemFixtures.DeleteTree(top);
        }
    }

    // ---- Undo only acts on a complete status answer

    [Fact]
    public async Task A_real_committed_and_a_real_unborn_status_are_both_usable()
    {
        var unborn = await GitService.GetStatusAsync(repo.Root);
        Assert.True(unborn.WellFormed);
        Assert.True(unborn.Unborn);
        Assert.Null(unborn.HeadOid);
        Assert.Equal("(initial)", unborn.HeadId);

        repo.Commit(("t.txt", "v1"));
        var committed = await GitService.GetStatusAsync(repo.Root);   // clean: no entries at all
        Assert.True(committed.WellFormed);
        Assert.False(committed.Unborn);
        Assert.Equal(repo.Git("rev-parse", "HEAD").Trim(), committed.HeadOid);
        Assert.Empty(committed.Staged);
    }

    public static TheoryData<string, string> Malformations => new()
    {
        { "no commit header", "drop-oid" },
        { "empty commit id", "oid:" },
        { "not a commit id", "oid:not-an-oid" },
        { "unknown marker", "oid:(unknown)" },
        { "not NUL-terminated", "chop" },
        { "unknown entry first", "unknown-first" },
        { "unknown entry last", "unknown-last" },
    };

    [Theory]
    [MemberData(nameof(Malformations))]
    public async Task Undo_on_incomplete_or_unexpected_status_output_changes_nothing(string _, string how)
    {
        repo.Commit(("t.txt", "v1"));
        repo.Write("t.txt", "v2");
        repo.Write("n.txt", "added");
        repo.Git("add", "-A");
        var confirmed = await Confirm(true);
        var raw = repo.Git("status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all");
        var records = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        var oid = records.FindIndex(r => r.StartsWith("# branch.oid "));
        switch (how)
        {
            case "drop-oid": records.RemoveAt(oid); break;
            case var s when s.StartsWith("oid:"): records[oid] = "# branch.oid " + s[4..]; break;
            case "unknown-first": records.Insert(0, "x future-entry"); break;
            case "unknown-last": records.Add("x future-entry"); break;
        }
        var text = string.Join('\0', records) + (how == "chop" ? "" : "\0");
        var status = GitService.ParseStatus(text);
        Assert.False(status.WellFormed);
        Assert.False(status.Unborn);
        Assert.Equal(2, status.Staged.Count);   // the entries themselves still parse: display can use them
        var before = repo.Snapshot();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitService.UndoAgainstAsync(repo.Root, status, true, confirmed.Files, confirmed.Head, confirmed.AlsoUnstaged));

        Assert.Equal(before, repo.Snapshot());
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task Unborn_confirmation_with_a_status_missing_its_commit_header_changes_nothing()
    {
        // Both "commits" are null here, so only recognising the response as incomplete can stop the delete-only undo.
        repo.Write("n.txt", "first");
        repo.Git("add", "n.txt");
        var confirmed = await Confirm(true);
        var raw = repo.Git("status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all");
        var text = string.Join('\0', raw.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(r => !r.StartsWith("# branch.oid "))) + "\0";
        var before = repo.Snapshot();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitService.UndoAgainstAsync(repo.Root, GitService.ParseStatus(text), true, confirmed.Files, confirmed.Head, confirmed.AlsoUnstaged));

        Assert.Equal(before, repo.Snapshot());
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task A_dialog_built_without_a_known_commit_cannot_undo_on_an_unborn_branch()
    {
        // The view showed a status whose commit was unknown; by the click git positively reports an unborn branch.
        // "Unknown" must not match "no commits yet".
        repo.Write("n.txt", "first");
        repo.Git("add", "n.txt");
        var files = (await GitService.GetStatusAsync(repo.Root)).Staged;
        var before = repo.Snapshot();

        await Assert.ThrowsAsync<InvalidOperationException>(() => GitService.UndoConfirmedAsync(repo.Root, true, files, confirmedHead: null, []));

        Assert.Equal(before, repo.Snapshot());
        Assert.Empty(recycled);
    }

    [Fact]
    public void Snapshot_sees_changes_to_dot_git_files_in_the_working_tree()
    {
        repo.Commit((".gitignore", "bin/\n"));
        repo.Write(".gitignore", "bin/\n# one comment\n");
        var before = repo.Snapshot();
        repo.Write(".gitignore", "bin/\n# another comment\n");   // still "modified": status and index look the same

        Assert.NotEqual(before, repo.Snapshot());
    }

    [Theory]
    [InlineData("")]
    [InlineData("# branch.oid 0123456789012345678901234567890123456789")]   // header only, cut short
    [InlineData("# branch.head main\0")]
    [InlineData("# branch.oid (initial)\0# branch.head main\0x unknown\0")]
    [InlineData("# branch.oid 0123456789012345678901234567890123456789\0" + "2 R. N... 100644 100644 100644 0123456789012345678901234567890123456789 0123456789012345678901234567890123456789 R100 b.txt\0")]   // rename without its old name
    public void Malformed_status_text_is_not_well_formed(string text) =>
        Assert.False(GitService.ParseStatus(text).WellFormed);

    [Fact]
    public async Task Real_status_with_renames_and_unusual_names_is_well_formed_and_parsed()
    {
        repo.Commit(("old name.txt", "content that git can match as a rename\n"), ("plain.txt", "p"));
        repo.Git("mv", "old name.txt", "1 new näme.txt");   // a name that starts like an entry marker
        repo.Write("u untracked ü.txt", "u");   // "u " is the unmerged-entry marker
        repo.Write("plain.txt", "p2");

        var status = await GitService.GetStatusAsync(repo.Root);

        Assert.True(status.WellFormed);
        var rename = Assert.Single(status.Staged);
        Assert.Equal(("1 new näme.txt", "old name.txt", "R"), (rename.Path, rename.OrigPath, rename.Status));
        Assert.Contains(status.Unstaged, f => f.Path == "u untracked ü.txt" && f.Untracked);
        Assert.Contains(status.Unstaged, f => f.Path == "plain.txt" && f.Status == "M");
    }

    // ---- A later step failing after an earlier one changed things is reported as partial

    private async Task<Confirmation> MixedStagedSelection()
    {
        repo.Commit(("t.txt", "v1"));
        repo.Write("t.txt", "v2");
        repo.Write("n.txt", "added");
        repo.Git("add", "-A");
        return await Confirm(true);
    }

    public static TheoryData<string> LaterFailures => new() { "git error", "empty error", "git would not start" };

    [Theory]
    [MemberData(nameof(LaterFailures))]
    public async Task A_later_git_step_failing_after_the_restore_says_Undo_was_only_partly_applied(string failure)
    {
        var confirmed = await MixedStagedSelection();
        FailStep("rm", () => failure switch
        {
            "git error" => Task.FromResult(new GitResult(128, [], "fatal: Unable to create '.git/index.lock': File exists.")),
            "empty error" => Task.FromResult(new GitResult(1, [], "")),
            _ => throw new InvalidOperationException("Could not start git"),
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Contains("only partly applied", ex.Message);
        Assert.Contains("n.txt", ex.Message);
        Assert.Equal("v1", repo.Read("t.txt"));   // the earlier step did happen
        Assert.True(repo.Exists("n.txt"));
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task Every_recycle_failing_after_a_staged_unstage_is_reported_as_partial()
    {
        repo.Commit(("base.txt", "b"));
        repo.Write("n.txt", "added");
        repo.Git("add", "n.txt");
        var confirmed = await Confirm(true);
        GitService.SendToRecycleBin = _ => throw new IOException("The Recycle Bin is not available.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Contains("only partly applied", ex.Message);
        Assert.True(repo.Exists("n.txt"));
        Assert.Equal("", repo.Git("diff", "--cached", "--name-only").Trim());   // it was unstaged before the move failed
    }

    [Fact]
    public async Task Every_recycle_failing_after_an_unstaged_restore_is_reported_as_partial()
    {
        repo.Commit(("t.txt", "v1"));
        repo.Write("t.txt", "v2");
        repo.Write("u.txt", "untracked");
        var confirmed = await Confirm(false);
        GitService.SendToRecycleBin = _ => throw new IOException("The Recycle Bin is not available.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.Contains("only partly applied", ex.Message);
        Assert.Equal("v1", repo.Read("t.txt"));
        Assert.True(repo.Exists("u.txt"));
    }

    [Fact]
    public async Task The_first_git_step_failing_is_reported_as_a_plain_failure_with_nothing_changed()
    {
        var confirmed = await MixedStagedSelection();
        var before = repo.Snapshot();
        FailStep("restore", () => Task.FromResult(new GitResult(1, [], "error: simulated")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Undo(confirmed));

        Assert.DoesNotContain("partly", ex.Message);
        Assert.Equal(before, repo.Snapshot());
        Assert.Empty(recycled);
    }
}
