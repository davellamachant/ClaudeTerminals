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
        repo.Dispose();
    }

    /// <summary>What the confirmation dialog shows and sends back: the files, the commit, and which have unstaged edits too.</summary>
    private sealed record Confirmation(bool Staged, List<FileChange> Files, string? Head, List<string> AlsoUnstaged);

    private async Task<Confirmation> Confirm(bool staged, Func<FileChange, bool>? select = null)
    {
        var st = await GitService.GetStatusAsync(repo.Root);
        var files = (staged ? st.Staged : st.Unstaged).Where(select ?? (_ => true)).ToList();
        var unstaged = st.Unstaged.Select(f => f.Path).ToHashSet();
        var alsoUnstaged = staged ? files.Where(f => unstaged.Contains(f.Path)).Select(f => f.Path).ToList() : [];
        return new Confirmation(staged, files, st.HeadOid, alsoUnstaged);
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
}
