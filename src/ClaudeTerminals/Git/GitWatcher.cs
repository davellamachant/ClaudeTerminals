namespace ClaudeTerminals.Git;

/// <summary>
/// Watches a worktree (and its external git dir, for linked worktrees) and raises a debounced
/// <see cref="Changed"/> event. A slow safety poll covers anything the file watcher misses.
/// </summary>
internal sealed class GitWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly System.Threading.Timer debounce;
    private readonly System.Threading.Timer poll;
    private volatile bool disposed;
    private int disposeStarted;

    public event Action? Changed;

    private static int live;

    /// <summary>Watchers created and not yet disposed (lets tests detect a leaked watcher).</summary>
    internal static int Live => Volatile.Read(ref live);

    public GitWatcher(string worktree, string? externalGitDir)
    {
        Interlocked.Increment(ref live);
        debounce = new System.Threading.Timer(_ => Raise(), null, Timeout.Infinite, Timeout.Infinite);
        poll = new System.Threading.Timer(_ => Raise(), null, 15000, 15000);
        TryWatch(worktree, true);
        if (externalGitDir != null) TryWatch(externalGitDir, false);
    }

    private void TryWatch(string path, bool recursive)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Changed += OnFsEvent;
            w.Created += OnFsEvent;
            w.Deleted += OnFsEvent;
            w.Renamed += OnFsEvent;
            w.Error += (_, _) => Kick();
            w.EnableRaisingEvents = true;
            watchers.Add(w);
        }
        catch (Exception)
        {
            // Watching is best-effort; the poll timer still refreshes.
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        // Object-store and log writes happen constantly during git operations and never change status by themselves.
        var p = e.FullPath;
        if (p.Contains(@"\.git\objects", StringComparison.OrdinalIgnoreCase) ||
            p.Contains(@"\.git\logs", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return;
        Kick();
    }

    private void Kick()
    {
        if (!disposed) debounce.Change(600, Timeout.Infinite);
    }

    private void Raise()
    {
        if (!disposed) Changed?.Invoke();
    }

    public void Dispose()
    {
        // Atomic test-and-set: a superseded InitGitAsync and session removal can dispose concurrently, and both
        // passing a plain check would decrement the live count twice.
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        Interlocked.Decrement(ref live);
        disposed = true;
        Changed = null;
        foreach (var w in watchers) w.Dispose();
        debounce.Dispose();
        poll.Dispose();
    }
}
