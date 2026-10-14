using ClaudeTerminals.Git;

namespace ClaudeTerminals.Tests;

/// <summary>Review findings F9 (unstaging renames), F10 (stdin deadlock) and F16 (oversized diffs), against real repositories.</summary>
/// <remarks>Not run alongside other tests: the memory test measures allocations process-wide.</remarks>
[Collection(nameof(GitUndoTests))]
public sealed class GitP2Tests : IDisposable
{
    private readonly TempRepo repo = new();

    public void Dispose() => repo.Dispose();

    private string Cached() => repo.Git("diff", "--cached", "--name-status").Trim();

    // ---- F9: unstaging a staged rename unstages both halves

    [Theory]
    [InlineData("a.txt", "b.txt")]
    [InlineData("old dir/ä file.txt", "new dir/b ü.txt")]   // across folders, spaces, non-ASCII
    public async Task Unstaging_the_new_name_unstages_the_whole_rename(string from, string to)
    {
        var body = string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i} of a file big enough for git to see the rename\n"));
        repo.Commit((from, body), ("other.txt", "o1\n"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo.Root, to))!);
        repo.Git("mv", from, to);
        repo.Write(to, body + "plus an edit\n");   // rename plus a small edit: still a rename to git
        repo.Git("add", "-A");
        Assert.Contains((await GitService.GetStatusAsync(repo.Root)).Staged, f => f.Status == "R" && f.Path == to && f.OrigPath == from);
        repo.Write("other.txt", "o2\n");
        repo.Git("add", "other.txt");                 // an unrelated staged change that must stay staged

        var r = await GitService.UnstageAsync(repo.Root, [to]);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("M\tother.txt", Cached());
        Assert.Equal(body + "plus an edit\n", repo.Read(to));   // working files untouched
        Assert.False(repo.Exists(from));
        repo.Git("commit", "-q", "-m", "only the unrelated change");
        Assert.Equal("other.txt", repo.Git("show", "--name-only", "--format=", "HEAD").Trim());
    }

    [Fact]
    public async Task Unstage_on_an_unreadable_status_changes_nothing()
    {
        repo.Commit(("a.txt", string.Concat(Enumerable.Repeat("a line long enough to be a rename\n", 30))));
        repo.Git("mv", "a.txt", "b.txt");
        var before = repo.Snapshot();
        var broken = GitService.ParseStatus("# branch.head main\0");   // e.g. a status read that failed half-way

        var r = await GitService.UnstageAgainstAsync(repo.Root, ["b.txt"], broken);

        Assert.False(r.Ok);
        Assert.Equal(before, repo.Snapshot());
    }

    [Fact]
    public async Task Unstage_all_with_renames_leaves_nothing_staged()
    {
        repo.Commit(("a.txt", "a\n"), ("c.txt", "c\n"));
        repo.Git("mv", "a.txt", "b.txt");
        repo.Git("mv", "c.txt", "d.txt");
        var staged = (await GitService.GetStatusAsync(repo.Root)).Staged.Select(f => f.Path).ToList();   // what "Unstage All" sends

        var r = await GitService.UnstageAsync(repo.Root, staged);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("", Cached());
    }

    // ---- F11: worktrees whose folder is gone are reported as prunable

    [Fact]
    public async Task A_worktree_whose_folder_was_deleted_is_marked_prunable()
    {
        repo.Commit(("a.txt", "a"));
        var live = Path.Combine(Path.GetTempPath(), "ct-wt-live-" + Guid.NewGuid().ToString("N")[..8]);
        var gone = Path.Combine(Path.GetTempPath(), "ct-wt-gone-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            repo.Git("worktree", "add", "-q", "-b", "live", live);
            repo.Git("worktree", "add", "-q", "-b", "gone", gone);
            FileSystemFixtures.DeleteTree(gone);

            var list = await GitService.GetWorktreesAsync(repo.Root);

            Assert.False(Assert.Single(list, w => w.Branch == "live").Prunable);
            Assert.True(Assert.Single(list, w => w.Branch == "gone").Prunable);
            Assert.False(Assert.Single(list, w => w.Branch == "main").Prunable);
        }
        finally
        {
            FileSystemFixtures.DeleteTree(live);
        }
    }

    // ---- F10: commands fed a large path list on stdin complete

    [Fact]
    public async Task A_command_that_answers_while_reading_a_large_input_completes()
    {
        repo.Commit(("a.txt", "a"));
        var input = string.Concat(Enumerable.Repeat("HEAD:a.txt\n", 20000));   // far beyond both pipe buffers

        var r = await GitService.RunAsync(repo.Root, ["cat-file", "--batch-check"], input).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(20000, r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Capped_output_with_a_large_input_ends_git_instead_of_hanging()
    {
        repo.Commit(("a.txt", "a"));
        var input = string.Concat(Enumerable.Repeat("HEAD:a.txt\n", 20000));

        var r = await GitService.RunAsync(repo.Root, ["cat-file", "--batch-check"], input, maxOutputBytes: 1000)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(r.Truncated);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task A_command_that_quits_before_reading_its_input_fails_without_hanging()
    {
        var notARepo = Path.Combine(Path.GetTempPath(), "ct-norepo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(notARepo);
        try
        {
            var input = string.Concat(Enumerable.Repeat("HEAD:a.txt\n", 20000));
            var r = await GitService.RunAsync(notARepo, ["cat-file", "--batch-check"], input).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(r.Ok);
        }
        finally
        {
            Directory.Delete(notARepo, true);
        }
    }

    [Fact]
    public async Task Staging_and_unstaging_thousands_of_paths_completes()
    {
        repo.Commit(("seed.txt", "s"));
        var paths = Enumerable.Range(0, 3000).Select(i => $"many/file {i:D4}.txt").ToList();
        foreach (var p in paths) repo.Write(p, p);

        var stage = await GitService.StageAsync(repo.Root, paths).WaitAsync(TimeSpan.FromSeconds(120));
        Assert.True(stage.Ok, stage.Error);
        Assert.Equal(3000, repo.Git("diff", "--cached", "--name-only").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        var unstage = await GitService.UnstageAsync(repo.Root, paths).WaitAsync(TimeSpan.FromSeconds(120));
        Assert.True(unstage.Ok, unstage.Error);
        Assert.Equal("", Cached());
    }

    // ---- F16: an oversized side is rejected without loading it

    private const int Limit = 4 * 1024 * 1024;

    [Theory]
    [InlineData(Limit - 1, false)]
    [InlineData(Limit, false)]
    [InlineData(Limit + 1, true)]
    public async Task The_size_limit_is_exact(int size, bool tooLarge)
    {
        File.WriteAllBytes(Path.Combine(repo.Root, "f.bin"), Bytes(size));
        repo.Git("add", "f.bin");

        var blob = await GitService.ShowAsync(repo.Root, ":f.bin");

        Assert.Equal(tooLarge, blob.TooLarge);
        Assert.Equal(tooLarge ? 0 : size, blob.Bytes.Length);
    }

    [Fact]
    public async Task A_huge_blob_is_not_read_into_memory()
    {
        File.WriteAllBytes(Path.Combine(repo.Root, "huge.bin"), Bytes(64 * 1024 * 1024));
        repo.Git("add", "huge.bin");
        await GitService.ShowAsync(repo.Root, ":huge.bin");   // warm up (JIT, git's first start)

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var blob = await GitService.ShowAsync(repo.Root, ":huge.bin");
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.True(blob.TooLarge);
        Assert.True(allocated < 24 * 1024 * 1024, $"allocated {allocated / (1024 * 1024)} MB to reject a 64 MB blob");
    }

    [Fact]
    public async Task A_huge_working_file_is_not_read_into_memory()
    {
        repo.Commit(("huge.bin", "small"));
        File.WriteAllBytes(Path.Combine(repo.Root, "huge.bin"), Bytes(64 * 1024 * 1024));
        await GitService.GetDiffAsync(repo.Root, "unstaged", "huge.bin", null, null, null);   // warm up

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var diff = await GitService.GetDiffAsync(repo.Root, "unstaged", "huge.bin", null, null, null);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal("File is too large to compare.", diff.Notice);
        Assert.True(allocated < 24 * 1024 * 1024, $"allocated {allocated / (1024 * 1024)} MB to reject a 64 MB working file");
    }

    [Theory]
    [InlineData("old")]
    [InlineData("new")]
    [InlineData("both")]
    public async Task An_oversized_side_shows_too_large_in_every_mode(string which)
    {
        var big = Bytes(Limit + 10);
        var small = "small\n"u8.ToArray();
        File.WriteAllBytes(Path.Combine(repo.Root, "f.txt"), which is "old" or "both" ? big : small);
        repo.Git("add", "f.txt");
        repo.Git("commit", "-q", "-m", "old side");
        File.WriteAllBytes(Path.Combine(repo.Root, "f.txt"), which is "new" or "both" ? Bytes(Limit + 20) : [.. small, .. "changed\n"u8]);
        repo.Git("add", "f.txt");
        repo.Git("commit", "-q", "-m", "new side");
        var head = repo.Git("rev-parse", "HEAD").Trim();
        var parent = repo.Git("rev-parse", "HEAD~1").Trim();

        var commit = await GitService.GetDiffAsync(repo.Root, "commit", "f.txt", null, head, parent);
        Assert.Equal("File is too large to compare.", commit.Notice);

        // Working tree vs index and index vs HEAD, with the oversized side in those places.
        repo.Git("reset", "-q", "--soft", "HEAD~1");
        var staged = await GitService.GetDiffAsync(repo.Root, "staged", "f.txt", null, null, null);
        Assert.Equal("File is too large to compare.", staged.Notice);
        repo.Git("reset", "-q");
        var unstaged = await GitService.GetDiffAsync(repo.Root, "unstaged", "f.txt", null, null, null);
        Assert.Equal("File is too large to compare.", unstaged.Notice);
    }

    private static byte[] Bytes(int size)
    {
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)('a' + i % 26);
        return b;
    }
}
