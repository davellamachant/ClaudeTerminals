using System.Diagnostics;
using System.Text;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Git;

internal sealed record GitResult(int ExitCode, byte[] Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Text => Encoding.UTF8.GetString(Output);

    /// <summary>Output went past the requested limit, so reading stopped and git was ended; <see cref="Output"/> is incomplete.</summary>
    public bool Truncated { get; init; }
}

/// <param name="Untracked">Not known to git at all ("?" in status); undoing it means deleting the file.</param>
/// <param name="InHead">Path exists in HEAD (status reports a non-zero HEAD mode); a staged undo restores it rather than deleting it.</param>
internal sealed record FileChange(string Path, string? OrigPath, string Status, bool Untracked = false, bool InHead = false);

/// <param name="HeadOid">The commit HEAD pointed at when this status was taken; null if there is none (see <paramref name="Unborn"/>).</param>
/// <param name="Unborn">Git positively reported a branch with no commits yet ("# branch.oid (initial)").</param>
/// <param name="WellFormed">
/// The output had everything expected: NUL-terminated, a commit header that is a commit id or "(initial)", and only
/// entry types this parser knows. Display tolerates less; anything that changes files (Undo) requires it.
/// </param>
internal sealed record GitStatus(
    bool IsRepo, string? Error, string? Branch, string? Upstream, int Ahead, int Behind,
    List<FileChange> Staged, List<FileChange> Unstaged, string? HeadOid = null, bool Unborn = false, bool WellFormed = false)
{
    /// <summary>What the page shows and sends back for the last commit: its id, "(initial)" on an unborn branch, or null when unknown.</summary>
    public string? HeadId => HeadOid ?? (Unborn ? "(initial)" : null);
}

internal sealed record CommitInfo(string Sha, string ShortSha, string Author, string Date, string Subject, string Refs, bool Outgoing);

/// <param name="Prunable">Git still lists the worktree but its folder is gone.</param>
internal sealed record WorktreeInfo(string Path, string? Branch, bool Bare, bool Prunable = false);

/// <summary>One worktree as the Project view shows it (see <see cref="GitService.GetWorktreeOverviewAsync"/>).</summary>
/// <param name="Main">The repository's main worktree (git lists it first); it can't be removed.</param>
/// <param name="Missing">Its folder is gone (git calls it prunable).</param>
/// <param name="Ahead">Commits on its branch that aren't on the base branch; 0 when there's no base branch or HEAD is detached.</param>
/// <param name="Behind">Commits on the base branch that aren't on its branch.</param>
/// <param name="Changes">Files git status lists in it (staged, unstaged and untracked); -1 if that couldn't be read.</param>
/// <param name="Merged">Everything on its branch is already on the base branch, so once it's clean it can go.</param>
internal sealed record WorktreeOverview(string Path, string? Branch, bool Main, bool Bare, bool Missing, int Ahead, int Behind, int Changes, bool Merged);

/// <param name="BaseBranch">What the worktrees are compared with (origin/main, say), or null if the repository has none.</param>
internal sealed record RepoOverview(string Root, string? BaseBranch, List<WorktreeOverview> Worktrees);

internal sealed record DiffContent(string Original, string Modified, string OriginalLabel, string ModifiedLabel, string? Notice);

/// <summary>Thin wrapper over git.exe. All calls are read-only unless the method name says otherwise.</summary>
internal static class GitService
{
    private const int MaxDiffBytes = 4 * 1024 * 1024;

    /// <param name="maxOutputBytes">Stop reading (and end git) once stdout exceeds this; the result is then <see cref="GitResult.Truncated"/>.</param>
    /// <param name="environment">Extra environment variables for git (e.g. to forbid credential prompts).</param>
    /// <param name="timeout">Ends git and returns a failed result if it runs longer than this.</param>
    public static async Task<GitResult> RunAsync(string dir, IEnumerable<string> args, string? stdin = null, CancellationToken ct = default,
        int? maxOutputBytes = null, IReadOnlyDictionary<string, string>? environment = null, TimeSpan? timeout = null)
    {
        if (timeout is { } limit)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(limit);
            try
            {
                return await RunAsync(dir, args, stdin, timer.Token, maxOutputBytes, environment);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new GitResult(1, [], $"git {args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('='))} was stopped after {limit.TotalSeconds:0} s without finishing.");
            }
        }
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Commit messages and pathspec lists go through stdin; without this they use the OEM code page.
        if (stdin != null) psi.StandardInputEncoding = new UTF8Encoding(false);
        // Don't take index.lock for read-only commands; the Claude sessions are using these repos concurrently.
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (environment != null)
            foreach (var (name, value) in environment) psi.Environment[name] = value;
        foreach (var a in new[] { "-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "core.safecrlf=false" })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git");
        void Kill()
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
        try
        {
            // Drain stdout and stderr before feeding stdin: git may answer while still reading its input (cat-file
            // --batch-check replies line by line), and with nobody reading, a full output pipe blocks both sides forever.
            // Past the output limit git is ended at once, which also releases a write to its stdin still in progress.
            using var ms = new MemoryStream();
            var stdoutTask = ReadOutputAsync(process.StandardOutput.BaseStream, ms, maxOutputBytes, Kill, ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            if (stdin != null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // git stopped reading (it failed, or was ended above); its exit code and stderr say why.
                }
            }
            var truncated = await stdoutTask;
            var error = await stderrTask;
            await process.WaitForExitAsync(ct);
            return new GitResult(truncated ? -1 : process.ExitCode, ms.ToArray(), error.Trim()) { Truncated = truncated };
        }
        catch
        {
            Kill();   // cancelled or failed part-way: don't leave git running (disposing Process doesn't end it)
            throw;
        }
    }

    /// <returns>True if reading stopped because <paramref name="max"/> was exceeded (and <paramref name="onLimit"/> was called).</returns>
    private static async Task<bool> ReadOutputAsync(Stream source, MemoryStream target, int? max, Action onLimit, CancellationToken ct)
    {
        if (max is not { } limit)
        {
            await source.CopyToAsync(target, ct);
            return false;
        }
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (target.Length + read > limit)
            {
                onLimit();
                return true;
            }
            target.Write(buffer, 0, read);
        }
        return false;
    }

    public static async Task<GitStatus> GetStatusAsync(string dir)
    {
        if (!Directory.Exists(dir))
            return new GitStatus(false, "Folder does not exist", null, null, 0, 0, [], []);

        var r = await RunAsync(dir, ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"]);
        if (!r.Ok)
        {
            var notRepo = r.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);
            return new GitStatus(false, notRepo ? null : r.Error, null, null, 0, 0, [], []);
        }
        return ParseStatus(r.Text);
    }

    /// <summary>Parses <c>git status --porcelain=v2 --branch -z</c> output.</summary>
    internal static GitStatus ParseStatus(string text)
    {
        string? branch = null, upstream = null, oid = null;
        int ahead = 0, behind = 0;
        var staged = new List<FileChange>();
        var unstaged = new List<FileChange>();
        var wellFormed = text.Length == 0 || text.EndsWith('\0');
        var entries = text.Split('\0');

        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Length == 0) continue;
            switch (e[0])
            {
                case '#':
                    if (e.StartsWith("# branch.head ")) branch = e[14..];
                    else if (e.StartsWith("# branch.oid ")) oid = e[13..];
                    else if (e.StartsWith("# branch.upstream ")) upstream = e[18..];
                    else if (e.StartsWith("# branch.ab "))
                    {
                        var parts = e[12..].Split(' ');
                        int.TryParse(parts[0].TrimStart('+'), out ahead);
                        int.TryParse(parts[1].TrimStart('-'), out behind);
                    }
                    break;
                case '1':
                {
                    var f = e.Split(' ', 9);
                    AddChange(f[1], f[8], null, f[3] != "000000", staged, unstaged);
                    break;
                }
                case '2':
                {
                    // The new side of a rename or copy is never in HEAD (f[3] is the original path's HEAD mode).
                    var f = e.Split(' ', 10);
                    var orig = i + 1 < entries.Length ? entries[++i] : null;
                    if (string.IsNullOrEmpty(orig)) wellFormed = false;
                    AddChange(f[1], f[9], orig, false, staged, unstaged);
                    break;
                }
                case 'u':
                {
                    var f = e.Split(' ', 11);
                    unstaged.Add(new FileChange(f[10], null, "U"));
                    break;
                }
                case '?':
                    unstaged.Add(new FileChange(e[2..], null, "A", Untracked: true));
                    break;
                case '!':   // ignored files (not requested)
                    break;
                default:
                    wellFormed = false;
                    break;
            }
        }

        if (branch == "(detached)" && oid != null) branch = $"(detached {oid[..Math.Min(8, oid.Length)]})";
        var headOid = oid is { Length: 40 or 64 } && oid.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0 ? oid : null;
        var unborn = oid == "(initial)";
        return new GitStatus(true, null, branch, upstream, ahead, behind, staged, unstaged, headOid, unborn,
            WellFormed: wellFormed && (headOid != null || unborn));
    }

    private static void AddChange(string xy, string path, string? orig, bool inHead, List<FileChange> staged, List<FileChange> unstaged)
    {
        if (xy[0] != '.') staged.Add(new FileChange(path, xy[0] is 'R' or 'C' ? orig : null, Letter(xy[0]), InHead: inHead));
        if (xy[1] != '.') unstaged.Add(new FileChange(path, null, Letter(xy[1]), InHead: inHead));
    }

    private static string Letter(char c) => c switch
    {
        'M' or 'T' => "M",
        'A' => "A",
        'D' => "D",
        'R' => "R",
        'C' => "A",
        _ => c.ToString(),
    };

    public static async Task<List<CommitInfo>> GetLogAsync(string dir, int max = 300)
    {
        var outgoing = new HashSet<string>();
        var up = await RunAsync(dir, ["rev-list", "@{upstream}..HEAD"]);
        if (up.Ok)
            foreach (var sha in up.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) outgoing.Add(sha.Trim());

        var r = await RunAsync(dir, ["log", $"-n{max}", "--format=%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1f%D%x1e", "HEAD"]);
        var list = new List<CommitInfo>();
        if (!r.Ok) return list;
        foreach (var rec in r.Text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = rec.Trim('\n', '\r').Split('\x1f');
            if (f.Length < 6) continue;
            list.Add(new CommitInfo(f[0], f[1], f[2], f[3], f[4], f[5], outgoing.Contains(f[0])));
        }
        return list;
    }

    public static async Task<object> GetCommitDetailsAsync(string dir, string sha)
    {
        var info = await RunAsync(dir, ["show", "-s", "--format=%H%x1f%an <%ae>%x1f%aI%x1f%B%x1f%P", sha]);
        var f = info.Text.Split('\x1f');
        var parents = f.Length > 4 ? f[4].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];

        var r = parents.Length > 0
            ? await RunAsync(dir, ["diff", "--name-status", "-z", "-M", parents[0], sha])
            : await RunAsync(dir, ["diff-tree", "--root", "-r", "-M", "--no-commit-id", "--name-status", "-z", sha]);

        var files = new List<FileChange>();
        var parts = r.Text.Split('\0');
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            var status = parts[i];
            if (status.Length == 0) continue;
            if (status[0] is 'R' or 'C')
            {
                if (i + 2 >= parts.Length) break;
                files.Add(new FileChange(parts[i + 2], parts[i + 1], status[0] == 'R' ? "R" : "A"));
                i += 2;
            }
            else
            {
                files.Add(new FileChange(parts[i + 1], null, Letter(status[0])));
                i += 1;
            }
        }

        return new
        {
            sha,
            author = f.Length > 1 ? f[1] : "",
            date = f.Length > 2 ? f[2] : "",
            message = f.Length > 3 ? f[3].TrimEnd() : "",
            parent = parents.FirstOrDefault(),
            files,
        };
    }

    public static async Task<List<WorktreeInfo>> GetWorktreesAsync(string dir)
    {
        var list = new List<WorktreeInfo>();
        var r = await RunAsync(dir, ["worktree", "list", "--porcelain"]);
        if (!r.Ok) return list;
        string? path = null, branch = null;
        bool bare = false, prunable = false;
        foreach (var raw in r.Text.Split('\n').Append(""))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (path != null) list.Add(new WorktreeInfo(path.Replace('/', '\\'), branch, bare, prunable));
                path = branch = null;
                bare = prunable = false;
            }
            else if (line.StartsWith("worktree ")) path = line[9..];
            else if (line.StartsWith("branch ")) branch = line[7..].Replace("refs/heads/", "");
            else if (line == "detached") branch = "(detached)";
            else if (line == "bare") bare = true;
            else if (line == "prunable" || line.StartsWith("prunable ")) prunable = true;   // its folder is gone
        }
        return list;
    }

    /// <summary>
    /// The branch the Project view compares worktrees with: what origin/HEAD points at (origin/main, say), else a local
    /// main or master; null when the repository has none of these.
    /// </summary>
    public static async Task<string?> GetBaseBranchAsync(string dir)
    {
        var head = await RunAsync(dir, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"]);
        if (head.Ok && head.Text.Trim() is { Length: > 0 } remote) return remote;
        foreach (var name in new[] { "main", "master" })
            if ((await RunAsync(dir, ["rev-parse", "--verify", "--quiet", "refs/heads/" + name])).Ok) return name;
        return null;
    }

    /// <summary>
    /// Every worktree of the repository containing <paramref name="dir"/>, with what the Project view shows for each:
    /// how far its branch is ahead of and behind <see cref="RepoOverview.BaseBranch"/>, and how many files are changed
    /// in it. The main worktree comes first, as git lists it. Read-only.
    /// </summary>
    public static async Task<RepoOverview> GetWorktreeOverviewAsync(string dir)
    {
        var here = await GetTopLevelAsync(dir) ?? dir;
        var baseBranch = await GetBaseBranchAsync(here);
        var list = await GetWorktreesAsync(here);
        var root = list.Count > 0 ? list[0].Path : here;   // the main worktree, whichever worktree this was asked from
        var rows = await Task.WhenAll(list.Select(async (w, i) =>
        {
            var exists = Directory.Exists(w.Path) && !w.Prunable;
            var onBranch = w.Branch != null && !w.Branch.StartsWith('(') && !w.Bare;
            var changes = exists && !w.Bare ? await CountChangesAsync(w.Path) : 0;
            var (ahead, behind) = baseBranch != null && onBranch ? await AheadBehindAsync(root, baseBranch, w.Branch!) : (0, 0);
            return new WorktreeOverview(w.Path, w.Branch, Main: i == 0, w.Bare, Missing: !exists, ahead, behind, changes,
                Merged: i > 0 && baseBranch != null && onBranch && ahead == 0);
        }));
        return new RepoOverview(root, baseBranch, rows.ToList());
    }

    /// <summary>Commits on <paramref name="branch"/> not on <paramref name="baseBranch"/>, and the other way round; (0, 0) if git couldn't say.</summary>
    private static async Task<(int Ahead, int Behind)> AheadBehindAsync(string dir, string baseBranch, string branch)
    {
        var r = await RunAsync(dir, ["rev-list", "--left-right", "--count", baseBranch + "..." + branch]);
        var parts = r.Ok ? r.Text.Trim().Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries) : [];
        return parts.Length == 2 && int.TryParse(parts[0], out var behind) && int.TryParse(parts[1], out var ahead) ? (ahead, behind) : (0, 0);
    }

    /// <summary>How many files git status lists in the worktree (staged, unstaged and untracked), or -1 if it couldn't say.</summary>
    private static async Task<int> CountChangesAsync(string worktree)
    {
        var r = await RunAsync(worktree, ["status", "--porcelain", "--untracked-files=normal"], maxOutputBytes: 1 << 20);
        return r.Ok && !r.Truncated ? r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : -1;
    }

    /// <summary>
    /// Removes the worktree at <paramref name="path"/> from the repository at <paramref name="repoDir"/> (the Project
    /// view's Remove worktree). Only a listed worktree other than the main one, and only when nothing in it is
    /// uncommitted: git's own check stays (never forced), and ours runs first so the refusal says what's in the way.
    /// A worktree whose folder is already gone is pruned from the list instead. The branch stays either way.
    /// </summary>
    public static async Task<GitResult> RemoveWorktreeAsync(string repoDir, string path)
    {
        if (!Path.IsPathFullyQualified(path)) return Failed("The worktree must be given as a full path.");
        var list = await GetWorktreesAsync(repoDir);
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var i = list.FindIndex(w => string.Equals(Path.GetFullPath(w.Path).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return Failed($"{path} isn't a worktree of this repository.");
        if (i == 0) return Failed("This is the repository's main worktree; it can't be removed.");
        if (!Directory.Exists(list[i].Path)) return await RunAsync(repoDir, ["worktree", "prune"]);
        var changes = await CountChangesAsync(list[i].Path);
        if (changes != 0)
            return Failed(changes < 0
                ? "Couldn't check the worktree for uncommitted changes, so it was left alone."
                : $"{changes} file{(changes == 1 ? " is" : "s are")} changed or new in this worktree and would be lost. Commit or undo {(changes == 1 ? "it" : "them")} first.");
        return await RunAsync(repoDir, ["worktree", "remove", list[i].Path], timeout: TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Creates a worktree of the repository at <paramref name="repoDir"/> in the new folder <paramref name="path"/>, on a
    /// new branch <paramref name="branch"/> started from <paramref name="startPoint"/> (the current commit when null).
    /// Changes the repository. The branch name, start point and folder are checked first, so git's options can't be
    /// smuggled in through them.
    /// </summary>
    public static async Task<GitResult> AddWorktreeAsync(string repoDir, string path, string branch, string? startPoint)
    {
        static GitResult Fail(string message) => new(1, [], message);
        if (branch.StartsWith('-') || !(await RunAsync(repoDir, ["check-ref-format", "--branch", branch])).Ok)
            return Fail($"\"{branch}\" isn't a valid branch name.");
        if ((await RunAsync(repoDir, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch])).Ok)
            return Fail($"A branch named \"{branch}\" already exists. Pick another name.");
        if (!Path.IsPathFullyQualified(path)) return Fail("The folder must be a full path, such as D:\\Source\\repo-feature.");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) return Fail($"{path} already exists and isn't empty.");
        if (File.Exists(path)) return Fail($"{path} is a file.");
        var args = new List<string> { "worktree", "add", "-b", branch, path };
        if (!string.IsNullOrWhiteSpace(startPoint))
        {
            if (startPoint.StartsWith('-') || !(await RunAsync(repoDir, ["rev-parse", "--verify", "--quiet", startPoint + "^{commit}"])).Ok)
                return Fail($"\"{startPoint}\" isn't a branch or commit in this repository.");
            args.Add(startPoint);
        }
        return await RunAsync(repoDir, args, timeout: TimeSpan.FromMinutes(5));   // checking out a big tree takes a while
    }

    /// <summary>
    /// Builds both sides of a diff. mode: "unstaged" (index → working tree), "staged" (HEAD → index),
    /// or "commit" (parent → commit).
    /// </summary>
    public static async Task<DiffContent> GetDiffAsync(string dir, string mode, string path, string? origPath, string? commit, string? parent)
    {
        var name = Path.GetFileName(path);
        var oldPath = origPath ?? path;
        switch (mode)
        {
            // The two sides are independent reads, each capped at MaxDiffBytes, so they run side by side.
            case "staged":
            {
                var head = ShowAsync(dir, $"HEAD:{oldPath}");
                var index = ShowAsync(dir, $":{path}");
                return Build(await head, await index, $"{oldPath};HEAD", $"{path};Staged");
            }
            case "commit":
            {
                var before = parent == null ? Task.FromResult(Blob.Empty) : ShowAsync(dir, $"{parent}:{oldPath}");
                var after = ShowAsync(dir, $"{commit}:{path}");
                var shortSha = commit?[..Math.Min(7, commit.Length)];
                var shortParent = parent?[..Math.Min(7, parent.Length)];
                return Build(await before, await after, $"{oldPath};{shortParent ?? "(none)"}", $"{path};{shortSha}");
            }
            default:
            {
                var work = ReadWorkingFileAsync(Path.Combine(dir, path));
                var index = await ShowAsync(dir, $":{path}");
                if (!index.Exists) index = await ShowAsync(dir, $"HEAD:{path}");
                return Build(index, await work, $"{path};Index", name);
            }
        }
    }

    /// <param name="TooLarge">Bigger than the viewer handles; its bytes were deliberately not loaded.</param>
    internal sealed record Blob(bool Exists, byte[] Bytes, bool TooLarge = false)
    {
        public static readonly Blob Empty = new(false, []);
        public static readonly Blob Large = new(true, [], TooLarge: true);
    }

    /// <summary>Reads one side of a diff from git, giving up (and ending git) as soon as it is too large to show.</summary>
    internal static async Task<Blob> ShowAsync(string dir, string spec)
    {
        var r = await RunAsync(dir, ["show", spec], maxOutputBytes: MaxDiffBytes);
        return r.Truncated ? Blob.Large : r.Ok ? new Blob(true, r.Output) : Blob.Empty;
    }

    private static async Task<Blob> ReadWorkingFileAsync(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath)) return Blob.Empty;
            // Read up to the limit rather than trusting a size checked earlier: the session may be writing the file
            // at this moment. Same capped reader as git's output, so both sides of a diff apply the limit identically.
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                81920, useAsync: true);
            using var content = new MemoryStream();
            if (await ReadOutputAsync(stream, content, MaxDiffBytes, static () => { }, CancellationToken.None)) return Blob.Large;
            return new Blob(true, content.ToArray());
        }
        catch (IOException)
        {
            return Blob.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return Blob.Empty;
        }
    }

    private static DiffContent Build(Blob original, Blob modified, string originalLabel, string modifiedLabel)
    {
        // Both readers stop at MaxDiffBytes and report TooLarge, so oversized bytes never get this far.
        if (original.TooLarge || modified.TooLarge)
            return new DiffContent("", "", originalLabel, modifiedLabel, "File is too large to compare.");
        if (IsBinary(original.Bytes) || IsBinary(modified.Bytes))
            return new DiffContent("", "", originalLabel, modifiedLabel, "Binary files differ.");
        return new DiffContent(Decode(original.Bytes), Decode(modified.Bytes), originalLabel, modifiedLabel, null);
    }

    private static bool IsBinary(byte[] bytes)
    {
        if (HasUtf16Bom(bytes)) return false;
        var n = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < n; i++)
            if (bytes[i] == 0) return true;
        return false;
    }

    private static bool HasUtf16Bom(byte[] b) => b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF));

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (HasUtf16Bom(bytes))
            return (bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // ---- Mutating operations (explicit user actions only) ----

    // Paths go through stdin (--pathspec-from-file) so "Stage All" on thousands of files can't overflow the
    // command line, and literal pathspecs stop names containing * or ? from being treated as globs.
    private static readonly string[] PathsFromStdin = ["--pathspec-from-file=-", "--pathspec-file-nul"];

    private static string NulList(IEnumerable<string> paths) => string.Join('\0', paths) + '\0';

    private static Task<GitResult> RunWithPathsAsync(string dir, IEnumerable<string> args, IReadOnlyCollection<string> paths) =>
        RunWithPaths(dir, args, paths);

    /// <summary>Runs one path-list git command. Replaceable so tests can make a single step of Undo fail.</summary>
    internal static Func<string, IEnumerable<string>, IReadOnlyCollection<string>, Task<GitResult>> RunWithPaths = (dir, args, paths) =>
        RunAsync(dir, ["--literal-pathspecs", .. args, .. PathsFromStdin], NulList(paths));

    public static Task<GitResult> StageAsync(string dir, IEnumerable<string> paths) =>
        RunWithPathsAsync(dir, ["add", "-A"], paths.ToList());

    public static async Task<GitResult> UnstageAsync(string dir, IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (!await HasHeadAsync(dir)) return await RunWithPathsAsync(dir, ["rm", "--cached", "-r", "-q"], list);
        return await UnstageAgainstAsync(dir, list, await GetStatusAsync(dir));
    }

    /// <summary><see cref="UnstageAsync"/> on a repository with commits, against a status already read (tests supply broken ones).</summary>
    internal static async Task<GitResult> UnstageAgainstAsync(string dir, List<string> list, GitStatus status)
    {
        // Without a complete status we can't tell which selected files are renames, and unstaging only their new
        // side would leave the old side's deletion staged: stop instead.
        if (!status.IsRepo || !status.WellFormed)
            return new GitResult(1, [], "Couldn't read the repository's status, so nothing was unstaged. Try again.");
        // A staged rename is two index changes: the old path deleted and the new one added. Unstaging only the new path
        // would leave the deletion staged for the next commit, so the old path is restored to the index too.
        var selected = list.ToHashSet(StringComparer.Ordinal);
        list.AddRange(status.Staged
            .Where(f => f.Status == "R" && f.OrigPath != null && selected.Contains(f.Path) && !selected.Contains(f.OrigPath))
            .Select(f => f.OrigPath!));
        return await RunWithPathsAsync(dir, ["restore", "--staged"], list);
    }

    private static async Task<bool> HasHeadAsync(string dir) =>
        (await RunAsync(dir, ["rev-parse", "--verify", "-q", "HEAD"])).Ok;

    /// <summary>
    /// Undo for the files the user confirmed. Status is re-read rather than trusting the page, and the undo is refused if
    /// a file no longer matches what was confirmed (see <see cref="SelectConfirmed"/>). For a staged undo, which restores
    /// from the last commit and also discards unstaged edits, the confirmation must also still describe the same commit
    /// and the same set of files with unstaged edits (the dialog warns about those).
    /// </summary>
    /// <param name="confirmedHead">The <see cref="GitStatus.HeadOid"/> the dialog was built from.</param>
    /// <param name="confirmedAlsoUnstaged">Selected files the dialog said also had unstaged edits.</param>
    /// <returns>The number of files undone.</returns>
    public static async Task<int> UndoConfirmedAsync(string root, bool staged, IReadOnlyList<FileChange> confirmed,
        string? confirmedHead = null, IReadOnlyCollection<string>? confirmedAlsoUnstaged = null) =>
        await UndoAgainstAsync(root, await GetStatusAsync(root), staged, confirmed, confirmedHead, confirmedAlsoUnstaged);

    /// <summary><see cref="UndoConfirmedAsync"/> against a status already read (tests supply malformed ones).</summary>
    internal static async Task<int> UndoAgainstAsync(string root, GitStatus status, bool staged, IReadOnlyList<FileChange> confirmed,
        string? confirmedHead, IReadOnlyCollection<string>? confirmedAlsoUnstaged)
    {
        if (!status.IsRepo) throw new InvalidOperationException(status.Error ?? "Not a Git repository.");
        // Only act on a complete answer: a branch with no commits must be reported as such, not inferred from a missing
        // commit header, and output that is cut short or holds entries this parser doesn't know could misclassify files.
        if (!status.WellFormed) throw new InvalidOperationException("Git's status output was incomplete or unexpected. Nothing was changed.");
        var items = SelectConfirmed(staged ? status.Staged : status.Unstaged, confirmed);
        if (items.Count == 0) return 0;
        if (staged)
        {
            // The dialog must have shown a known commit (or a positively unborn branch), and it must still be the current one.
            if (confirmedHead == null)
                throw new InvalidOperationException("The view didn't know the last commit when you chose Undo. Refresh and try again.");
            if (status.HeadId != confirmedHead)
                throw new InvalidOperationException("The last commit changed after you chose Undo. Review the changes and try again.");
            var unstagedNow = status.Unstaged.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var warned = (confirmedAlsoUnstaged ?? []).ToHashSet(StringComparer.Ordinal);
            if (items.FirstOrDefault(i => unstagedNow.Contains(i.Path) != warned.Contains(i.Path)) is { } changed)
                throw new InvalidOperationException($"\"{changed.Path}\" changed after you chose Undo. Review the changes and try again.");
        }
        var r = await UndoAsync(root, staged, items, status.HeadOid);
        if (!r.Ok) throw new InvalidOperationException(string.IsNullOrEmpty(r.Error) ? "Undo failed" : r.Error);
        return items.Count;
    }

    /// <summary>
    /// Matches the files the user confirmed for Undo against a fresh status, so Undo performs the action the user saw.
    /// If a confirmed file's state has changed since (e.g. a tracked edit is now an untracked file, which undo deletes),
    /// this throws rather than silently doing something else. Files with no change any more, or now conflicted, are skipped.
    /// </summary>
    public static List<FileChange> SelectConfirmed(IEnumerable<FileChange> current, IEnumerable<FileChange> confirmed)
    {
        var now = new Dictionary<string, FileChange>(StringComparer.Ordinal);
        foreach (var f in current) now.TryAdd(f.Path, f);
        var items = new List<FileChange>();
        foreach (var c in confirmed)
        {
            if (!now.TryGetValue(c.Path, out var f) || f.Status == "U") continue;
            if (f != c) throw new InvalidOperationException($"\"{c.Path}\" changed after you chose Undo. Review the changes and try again.");
            items.Add(f);
        }
        return items;
    }

    /// <summary>
    /// Visual Studio's "Undo Changes". Unstaged: tracked files go back to their staged (index) version and untracked
    /// files are deleted. Staged: files go back to HEAD in both index and working tree, which also discards any
    /// unstaged edits to them; files that don't exist in HEAD (added, or the new side of a rename) are deleted.
    /// Deleted files go to the Recycle Bin rather than being removed outright.
    /// </summary>
    /// <param name="items">Entries from the status the user confirmed (see <see cref="SelectConfirmed"/>).</param>
    /// <param name="headOid">That status's <see cref="GitStatus.HeadOid"/>: staged files are restored from this commit, not whatever HEAD is by now.</param>
    public static async Task<GitResult> UndoAsync(string root, bool staged, IReadOnlyList<FileChange> items, string? headOid = null)
    {
        var restore = new List<string>();
        var delete = new List<string>();

        if (!staged)
        {
            foreach (var item in items)
                (item.Untracked ? delete : restore).Add(item.Path);
            if (restore.Count > 0)
            {
                var r = await RunWithPathsAsync(root, ["restore", "--worktree"], restore);
                if (!r.Ok) return r;
            }
        }
        else
        {
            // Whether a path is in HEAD comes from the status snapshot itself, never from a second lookup: a lookup that
            // fails or comes back partial must not turn "restore" into "delete".
            foreach (var item in items)
            {
                // Undoing a rename restores the old name. A copy's source was not changed by the copy, so it is left alone.
                if (item.Status == "R" && item.OrigPath != null) restore.Add(item.OrigPath);
                (item.InHead ? restore : delete).Add(item.Path);
            }
            restore = restore.Distinct().ToList();
            delete = delete.Distinct().ToList();
            if (restore.Count > 0 && headOid == null)
                return new GitResult(1, [], "Could not tell which commit to restore from. Nothing was changed.");

            // Restoring a path the index has deleted (the old side of a rename, or a staged deletion) writes HEAD's version
            // over whatever is on disk there. If a file exists there again, refuse rather than overwrite it. The one
            // exception is a case-only rename in a case-insensitive folder, where the file found at the old name is the
            // renamed file itself (whose unstaged edits the confirmation covers); that is decided by the file system's
            // identity of the two names, not by their spelling, because folders can be case-sensitive.
            var occupied = new List<string>();
            foreach (var item in items)
            {
                var removed = item.Status == "R" ? item.OrigPath : item.Status == "D" ? item.Path : null;
                if (removed == null || !ExistsInWorkingTree(root, removed)) continue;
                if (item.Status == "R" && SameFile(root, removed, item.Path)) continue;
                occupied.Add(removed);
            }
            if (occupied.Count > 0)
                return new GitResult(1, [], "Undo would overwrite a file that now exists at the original name. Move or rename it first:\n" + string.Join('\n', occupied));

            // Restore straight after that check, then unstage the new files (with --literal-pathspecs this removes only the
            // exact index entry, so a case-only rename's restored name survives).
            if (restore.Count > 0)
            {
                var r = await RunWithPathsAsync(root, ["restore", $"--source={headOid}", "--staged", "--worktree"], restore);
                if (!r.Ok) return r;
            }
            if (delete.Count > 0)
            {
                GitResult r;
                try
                {
                    r = await RunWithPathsAsync(root, ["rm", "--cached", "-q", "-f", "--ignore-unmatch"], delete);
                }
                catch (Exception ex) when (restore.Count > 0)   // e.g. git could not be started: the restore still happened
                {
                    r = new GitResult(-1, [], ex.Message);
                }
                if (!r.Ok)
                    return restore.Count == 0 ? r : PartlyApplied(
                        $"{restore.Count} file(s) were restored from the last commit, but these could not be unstaged:\n{string.Join('\n', delete)}" +
                        (string.IsNullOrWhiteSpace(r.Error) ? "" : "\n" + r.Error));
            }
            // After a case-only rename in a case-insensitive folder, the file just restored is also the one at the new
            // name, so it must not go to the Recycle Bin.
            delete.RemoveAll(d => restore.Any(r => string.Equals(r, d, StringComparison.OrdinalIgnoreCase) && SameFile(root, r, d)));
        }

        var failed = new List<string>();
        foreach (var path in delete)
            if (!TryRecycle(root, path)) failed.Add(path);
        if (failed.Count == 0) return new GitResult(0, [], "");
        var notMoved = "Could not move to the Recycle Bin:\n" + string.Join('\n', failed);
        // A staged undo has already unstaged these files; otherwise something was done unless every step was a failed move.
        var nothingElseDone = !staged && restore.Count == 0 && failed.Count == delete.Count;
        return nothingElseDone ? new GitResult(1, [], notMoved) : PartlyApplied(notMoved);
    }

    private static GitResult PartlyApplied(string detail) => new(1, [], "Undo was only partly applied. " + detail);

    /// <summary>
    /// True if two repository-relative names are the same existing directory entry (e.g. a.txt and A.txt in a
    /// case-insensitive folder). A link is not followed: a symbolic link at one name pointing at the other is a different entry.
    /// </summary>
    private static bool SameFile(string root, string a, string b) =>
        FullPathInRepo(root, a) is { } fa && FullPathInRepo(root, b) is { } fb &&
        Native.FinalPath(fa, followFinalLink: false) is { } finalA &&
        string.Equals(finalA, Native.FinalPath(fb, followFinalLink: false), StringComparison.Ordinal);

    /// <summary>Full path of a repository-relative path, or null unless it names something inside <paramref name="root"/>.</summary>
    /// <remarks>Ordinal: a path that leaves the repository and comes back with different casing may be another folder.</remarks>
    public static string? FullPathInRepo(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        return full.Length > fullRoot.Length && full.StartsWith(fullRoot, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// Like <see cref="FullPathInRepo"/>, but also follows junctions and symbolic links: null unless the existing file or
    /// folder the path really names is inside the repository (whose own path may itself go through a link).
    /// </summary>
    public static string? ExistingPathInRepo(string root, string relativePath)
    {
        var full = FullPathInRepo(root, relativePath);
        if (full == null || Native.FinalPath(root) is not { } finalRoot || Native.FinalPath(full) is not { } final) return null;
        finalRoot = finalRoot.TrimEnd('\\') + "\\";
        // Both paths come back in the file system's own casing, so compare exactly: in a case-sensitive parent,
        // C:\work\Repo is a different folder from C:\work\repo.
        return final.Length > finalRoot.Length && final.StartsWith(finalRoot, StringComparison.Ordinal) ? full : null;
    }

    private static bool ExistsInWorkingTree(string root, string relativePath) =>
        FullPathInRepo(root, relativePath) is { } full && (File.Exists(full) || Directory.Exists(full));

    /// <summary>Moves one file to the Recycle Bin. Replaceable so tests don't fill the real Recycle Bin.</summary>
    internal static Action<string> SendToRecycleBin = full =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    private static bool TryRecycle(string root, string relativePath)
    {
        var full = FullPathInRepo(root, relativePath);
        if (full == null) return false;   // never touch anything outside the repo
        try
        {
            if (!File.Exists(full)) return true;
            if (ExistingPathInRepo(root, relativePath) == null) return false;   // ...including through a junction or link
            SendToRecycleBin(full);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- Remote operations (explicit user actions; they reach the network and may show a credential prompt) ----

    /// <summary>Long enough to sign in through Git Credential Manager's window; short enough that nothing hangs forever.</summary>
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromMinutes(5);

    public static Task<GitResult> FetchAsync(string dir) => RunRemoteAsync(dir, ["fetch", "--prune"]);

    /// <summary>
    /// Fast-forward only: never creates a merge commit or leaves conflicts in a worktree a Claude session may be editing.
    /// A diverged branch fails with git's explanation instead.
    /// </summary>
    public static Task<GitResult> PullAsync(string dir) => RunRemoteAsync(dir, ["pull", "--ff-only"]);

    /// <summary>Never forces. A branch with no upstream is published to the default remote, which then becomes its upstream.</summary>
    public static async Task<GitResult> PushAsync(string dir)
    {
        var status = await GetStatusAsync(dir);
        if (!status.IsRepo) return Failed("This folder isn't a Git repository.");
        if (status.Branch is null || status.Branch.StartsWith('('))
            return Failed("There's no branch to push: HEAD is detached. Check out a branch first.");
        if (status.Upstream != null) return await RunRemoteAsync(dir, ["push"]);
        var remote = await DefaultRemoteAsync(dir);
        return remote == null
            ? Failed("This repository has no remote to push to.")
            : await RunRemoteAsync(dir, ["push", "--set-upstream", remote, "HEAD"]);
    }

    /// <summary>"origin" if it exists, otherwise the first remote, or null when there are none.</summary>
    private static async Task<string?> DefaultRemoteAsync(string dir)
    {
        var remotes = (await RunAsync(dir, ["remote"])).Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
    }

    private static Task<GitResult> RunRemoteAsync(string dir, string[] args) => RunAsync(dir, args, timeout: RemoteTimeout);

    private static GitResult Failed(string message) => new(1, [], message);

    public static async Task<GitResult> CommitAsync(string dir, string message, bool all)
    {
        if (all)
        {
            var add = await RunAsync(dir, ["add", "-A"]);
            if (!add.Ok) return add;
        }
        return await RunAsync(dir, ["commit", "-F", "-"], message);
    }

    /// <summary>Status paths are relative to the repository root, so all file-level commands run there.</summary>
    public static async Task<string?> GetTopLevelAsync(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var r = await RunAsync(dir, ["rev-parse", "--show-toplevel"]);
        return r.Ok ? Path.GetFullPath(r.Text.Trim()) : null;
    }

    /// <summary>Folders never searched for nested repositories: build output and package caches are big and hold none worth showing.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "packages", ".vs", ".idea", ".vscode", "dist", "build", "out", "target", "vendor", "__pycache__",
    };

    /// <summary>
    /// The repositories a session in <paramref name="dir"/> can show: the one containing it (if any) and any repositories in
    /// folders below it, down to <paramref name="maxDepth"/> levels. A repository's own subfolders are searched too
    /// (submodules, repositories checked out inside another). Junctions and links aren't followed, so the search can't
    /// loop or wander off the folder. Full paths, the containing repository first, then by path.
    /// </summary>
    public static async Task<List<string>> FindRepositoriesAsync(string dir, int maxDepth = 4, int maxFolders = 5000)
    {
        var found = new List<string>();
        if (!Directory.Exists(dir)) return found;
        if (await GetTopLevelAsync(dir) is { } top) found.Add(top);
        var nested = await Task.Run(() => FindNestedRepositories(Path.GetFullPath(dir), maxDepth, maxFolders));
        found.AddRange(nested.Where(r => !found.Contains(r, StringComparer.OrdinalIgnoreCase)));
        return found;
    }

    internal static List<string> FindNestedRepositories(string dir, int maxDepth, int maxFolders)
    {
        var list = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((dir, 0));
        var visited = 0;
        while (queue.Count > 0 && visited++ < maxFolders)
        {
            var (path, depth) = queue.Dequeue();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(path).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || SkippedFolders.Contains(name)) continue;
                try
                {
                    if (new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                // A folder is a repository's top when it has .git: a folder, or a file for linked worktrees and submodules.
                var git = Path.Combine(child, ".git");
                if (Directory.Exists(git) || File.Exists(git)) list.Add(child);
                if (depth + 1 < maxDepth) queue.Enqueue((child, depth + 1));
            }
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>Returns the git dir if it lives outside the worktree (linked worktrees), so it can be watched too.</summary>
    public static async Task<string?> GetExternalGitDirAsync(string dir)
    {
        var r = await RunAsync(dir, ["rev-parse", "--absolute-git-dir"]);
        if (!r.Ok) return null;
        var gitDir = Path.GetFullPath(r.Text.Trim());
        var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        return gitDir.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? null : gitDir;
    }
}
