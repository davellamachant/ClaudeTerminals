using System.Diagnostics;
using System.Text;

namespace ClaudeTerminals.Git;

internal sealed record GitResult(int ExitCode, byte[] Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Text => Encoding.UTF8.GetString(Output);
}

/// <param name="Untracked">Not known to git at all ("?" in status); undoing it means deleting the file.</param>
internal sealed record FileChange(string Path, string? OrigPath, string Status, bool Untracked = false);

internal sealed record GitStatus(
    bool IsRepo, string? Error, string? Branch, string? Upstream, int Ahead, int Behind,
    List<FileChange> Staged, List<FileChange> Unstaged);

internal sealed record CommitInfo(string Sha, string ShortSha, string Author, string Date, string Subject, string Refs, bool Outgoing);

internal sealed record WorktreeInfo(string Path, string? Branch, bool Bare);

internal sealed record DiffContent(string Original, string Modified, string OriginalLabel, string ModifiedLabel, string? Notice);

/// <summary>Thin wrapper over git.exe. All calls are read-only unless the method name says otherwise.</summary>
internal static class GitService
{
    private const int MaxDiffBytes = 4 * 1024 * 1024;

    public static async Task<GitResult> RunAsync(string dir, IEnumerable<string> args, string? stdin = null, CancellationToken ct = default)
    {
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
        foreach (var a in new[] { "-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "core.safecrlf=false" })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git");
        if (stdin != null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        using var ms = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);
        return new GitResult(process.ExitCode, ms.ToArray(), stderrTask.Result.Trim());
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

        string? branch = null, upstream = null, oid = null;
        int ahead = 0, behind = 0;
        var staged = new List<FileChange>();
        var unstaged = new List<FileChange>();
        var entries = r.Text.Split('\0');

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
                    AddChange(f[1], f[8], null, staged, unstaged);
                    break;
                }
                case '2':
                {
                    var f = e.Split(' ', 10);
                    var orig = i + 1 < entries.Length ? entries[++i] : null;
                    AddChange(f[1], f[9], orig, staged, unstaged);
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
            }
        }

        if (branch == "(detached)" && oid != null) branch = $"(detached {oid[..Math.Min(8, oid.Length)]})";
        return new GitStatus(true, null, branch, upstream, ahead, behind, staged, unstaged);
    }

    private static void AddChange(string xy, string path, string? orig, List<FileChange> staged, List<FileChange> unstaged)
    {
        if (xy[0] != '.') staged.Add(new FileChange(path, xy[0] is 'R' or 'C' ? orig : null, Letter(xy[0])));
        if (xy[1] != '.') unstaged.Add(new FileChange(path, null, Letter(xy[1])));
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
        var bare = false;
        foreach (var raw in r.Text.Split('\n').Append(""))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (path != null) list.Add(new WorktreeInfo(path.Replace('/', '\\'), branch, bare));
                path = branch = null;
                bare = false;
            }
            else if (line.StartsWith("worktree ")) path = line[9..];
            else if (line.StartsWith("branch ")) branch = line[7..].Replace("refs/heads/", "");
            else if (line == "detached") branch = "(detached)";
            else if (line == "bare") bare = true;
        }
        return list;
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
            case "staged":
            {
                var head = await ShowAsync(dir, $"HEAD:{oldPath}");
                var index = await ShowAsync(dir, $":{path}");
                return Build(head, index, $"{oldPath};HEAD", $"{path};Staged");
            }
            case "commit":
            {
                var before = parent == null ? Blob.Empty : await ShowAsync(dir, $"{parent}:{oldPath}");
                var after = await ShowAsync(dir, $"{commit}:{path}");
                var shortSha = commit?[..Math.Min(7, commit.Length)];
                var shortParent = parent?[..Math.Min(7, parent.Length)];
                return Build(before, after, $"{oldPath};{shortParent ?? "(none)"}", $"{path};{shortSha}");
            }
            default:
            {
                var index = await ShowAsync(dir, $":{path}");
                if (!index.Exists) index = await ShowAsync(dir, $"HEAD:{path}");
                var work = ReadWorkingFile(Path.Combine(dir, path));
                return Build(index, work, $"{path};Index", name);
            }
        }
    }

    private sealed record Blob(bool Exists, byte[] Bytes)
    {
        public static readonly Blob Empty = new(false, []);
    }

    private static async Task<Blob> ShowAsync(string dir, string spec)
    {
        var r = await RunAsync(dir, ["show", spec]);
        return r.Ok ? new Blob(true, r.Output) : Blob.Empty;
    }

    private static Blob ReadWorkingFile(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath)) return Blob.Empty;
            var info = new FileInfo(fullPath);
            if (info.Length > MaxDiffBytes) return new Blob(true, new byte[MaxDiffBytes + 1]);
            return new Blob(true, File.ReadAllBytes(fullPath));
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
        if (original.Bytes.Length > MaxDiffBytes || modified.Bytes.Length > MaxDiffBytes)
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
        RunAsync(dir, ["--literal-pathspecs", .. args, .. PathsFromStdin], NulList(paths));

    public static Task<GitResult> StageAsync(string dir, IEnumerable<string> paths) =>
        RunWithPathsAsync(dir, ["add", "-A"], paths.ToList());

    public static async Task<GitResult> UnstageAsync(string dir, IEnumerable<string> paths)
    {
        var list = paths.ToList();
        return await HasHeadAsync(dir)
            ? await RunWithPathsAsync(dir, ["restore", "--staged"], list)
            : await RunWithPathsAsync(dir, ["rm", "--cached", "-r", "-q"], list);
    }

    private static async Task<bool> HasHeadAsync(string dir) =>
        (await RunAsync(dir, ["rev-parse", "--verify", "-q", "HEAD"])).Ok;

    /// <summary>
    /// Visual Studio's "Undo Changes". Unstaged: tracked files go back to their staged (index) version and untracked
    /// files are deleted. Staged: files go back to HEAD in both index and working tree, which also discards any
    /// unstaged edits to them; files that don't exist in HEAD (added, or the new side of a rename) are deleted.
    /// Deleted files go to the Recycle Bin rather than being removed outright.
    /// </summary>
    public static async Task<GitResult> UndoAsync(string root, bool staged, IReadOnlyList<FileChange> items)
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
            var candidates = items.SelectMany(i => i.OrigPath != null ? new[] { i.OrigPath, i.Path } : [i.Path]).Distinct().ToList();
            var inHead = await PathsInHeadAsync(root, candidates);
            foreach (var path in candidates)
                (inHead.Contains(path) ? restore : delete).Add(path);

            if (restore.Count > 0)
            {
                var r = await RunWithPathsAsync(root, ["restore", "--source=HEAD", "--staged", "--worktree"], restore);
                if (!r.Ok) return r;
            }
            if (delete.Count > 0)
            {
                var r = await RunWithPathsAsync(root, ["rm", "--cached", "-q", "-f", "--ignore-unmatch"], delete);
                if (!r.Ok) return r;
            }
        }

        var failed = new List<string>();
        foreach (var path in delete)
            if (!TryRecycle(root, path)) failed.Add(path);
        return failed.Count == 0
            ? new GitResult(0, [], "")
            : new GitResult(1, [], "Could not move to the Recycle Bin:\n" + string.Join('\n', failed));
    }

    private static async Task<HashSet<string>> PathsInHeadAsync(string root, IReadOnlyCollection<string> paths)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (paths.Count == 0 || !await HasHeadAsync(root)) return set;
        // One line out per line in: "<sha> <type> <size>" when HEAD has the path, "<spec> missing" when it doesn't.
        var list = paths.ToList();
        var r = await RunAsync(root, ["cat-file", "--batch-check"], string.Concat(list.Select(p => $"HEAD:{p}\n")));
        var lines = r.Text.Split('\n');
        for (var i = 0; i < list.Count && i < lines.Length; i++)
            if (!lines[i].TrimEnd('\r').EndsWith(" missing", StringComparison.Ordinal)) set.Add(list[i]);
        return set;
    }

    private static bool TryRecycle(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;   // never touch anything outside the repo
        try
        {
            if (File.Exists(full))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

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
