using ClaudeTerminals.Git;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Sessions;

/// <summary>Runtime state for one configured session: its pseudo console process and its git watcher.</summary>
internal sealed class TerminalSession : IDisposable
{
    private PseudoConsoleProcess? process;
    private GitWatcher? watcher;
    private int statusRunning;
    private int statusDirty;

    public TerminalSession(SessionInfo info) => Info = info;

    public SessionInfo Info { get; }
    public string Id => Info.Id;
    public bool IsRunning => process != null;
    public string? RepoRoot { get; private set; }

    /// <summary>Raised on a background thread.</summary>
    public event Action<TerminalSession, string>? Output;
    public event Action<TerminalSession, int>? Exited;
    public event Action<TerminalSession, GitStatus>? StatusChanged;

    public void Start(int cols, int rows)
    {
        if (process != null) return;
        var p = new PseudoConsoleProcess();
        p.Output += text => Output?.Invoke(this, text);
        p.Exited += code =>
        {
            if (Interlocked.CompareExchange(ref process, null, p) == p) p.Dispose();
            Exited?.Invoke(this, code);
        };
        process = p;
        try
        {
            var env = SessionEnvironment.Build();
            p.Start(CommandLineResolver.Resolve(Info.Command, env), Info.Directory, SessionEnvironment.ToBlock(env), cols, rows);
        }
        catch
        {
            process = null;
            p.Dispose();
            throw;
        }
    }

    public void Write(string data) => process?.Write(data);

    public void Resize(int cols, int rows) => process?.Resize(cols, rows);

    public void Stop()
    {
        var p = Interlocked.Exchange(ref process, null);
        p?.Dispose();
    }

    public async Task InitGitAsync()
    {
        watcher?.Dispose();
        watcher = null;
        RepoRoot = await GitService.GetTopLevelAsync(Info.Directory);
        var external = RepoRoot != null ? await GitService.GetExternalGitDirAsync(RepoRoot) : null;
        // Watch the session folder even if it isn't a repo yet, so a later `git init` is picked up.
        if (Directory.Exists(Info.Directory))
        {
            watcher = new GitWatcher(RepoRoot ?? Info.Directory, external);
            watcher.Changed += () => _ = RefreshStatusAsync();
        }
        await RefreshStatusAsync();
    }

    /// <summary>Recomputes git status; overlapping requests are coalesced into one follow-up run.</summary>
    public async Task RefreshStatusAsync()
    {
        Interlocked.Exchange(ref statusDirty, 1);
        if (Interlocked.Exchange(ref statusRunning, 1) == 1) return;
        try
        {
            while (Interlocked.Exchange(ref statusDirty, 0) == 1)
            {
                if (RepoRoot == null)
                {
                    RepoRoot = await GitService.GetTopLevelAsync(Info.Directory);
                }
                var status = await GitService.GetStatusAsync(RepoRoot ?? Info.Directory);
                StatusChanged?.Invoke(this, status);
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, new GitStatus(false, ex.Message, null, null, 0, 0, [], []));
        }
        finally
        {
            Interlocked.Exchange(ref statusRunning, 0);
        }
    }

    public void Dispose()
    {
        Output = null;
        Exited = null;
        StatusChanged = null;
        watcher?.Dispose();
        Stop();
    }
}
