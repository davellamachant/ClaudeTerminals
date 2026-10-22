using ClaudeTerminals.Git;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Sessions;

/// <summary>Runtime state for one configured session: its pseudo console process and its git watcher.</summary>
internal sealed class TerminalSession : IDisposable
{
    private PseudoConsoleProcess? process;
    private GitWatcher? watcher;
    private readonly object gitLock = new();
    private int gitGeneration;
    private bool disposed;
    private int statusRunning;
    private int statusDirty;

    public TerminalSession(SessionInfo info) => Info = info;

    public SessionInfo Info { get; }
    public string Id => Info.Id;
    public bool IsRunning => process != null;
    public string? RepoRoot { get; private set; }

    /// <summary>
    /// A conversation to reopen at the next start, once: set after an update restarted the app, so a plain "claude"
    /// session carries on where it was instead of starting a new conversation.
    /// </summary>
    public string? ResumeOnce { get; set; }

    /// <summary>Counts processes started; <see cref="Exited"/> carries the number of the one that exited.</summary>
    public int Generation { get; private set; }

    /// <summary>Raised on a background thread.</summary>
    public event Action<TerminalSession, string>? Output;
    /// <summary>(session, exit code, generation of the process that exited). Raised on a background thread.</summary>
    public event Action<TerminalSession, int, int>? Exited;
    public event Action<TerminalSession, GitStatus>? StatusChanged;

    public void Start(int cols, int rows)
    {
        if (process != null) return;
        var generation = ++Generation;
        var p = new PseudoConsoleProcess();
        p.Output += text => Output?.Invoke(this, text);
        p.Exited += code =>
        {
            // If Stop/restart already took this process, its late exit must not be reported against the next one.
            if (Interlocked.CompareExchange(ref process, null, p) != p) return;
            p.Dispose();
            Exited?.Invoke(this, code, generation);
        };
        process = p;
        try
        {
            var env = SessionEnvironment.Build();
            var command = WithResume(Info.Command, ResumeOnce);
            ResumeOnce = null;
            p.Start(CommandLineResolver.Resolve(command, env), Info.Directory, SessionEnvironment.ToBlock(env), cols, rows);
        }
        catch
        {
            process = null;
            p.Dispose();
            throw;
        }
    }

    public void Write(string data) => process?.Write(data);

    /// <summary>
    /// Adds --resume to a plain claude command. Anything that already picks its conversation (or isn't an interactive
    /// claude) is left alone.
    /// </summary>
    internal static string WithResume(string command, string? conversationId)
    {
        if (conversationId == null || !System.Text.RegularExpressions.Regex.IsMatch(conversationId, "^[A-Za-z0-9-]{1,100}$")) return command;
        var tokens = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !(tokens[0].Equals("claude", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("claude.exe", StringComparison.OrdinalIgnoreCase)))
            return command;
        string[] picksItsOwn = ["-r", "--resume", "-c", "--continue", "-p", "--print", "--session-id", "--fork-session"];
        if (tokens.Skip(1).Any(t => picksItsOwn.Contains(t.Split('=')[0], StringComparer.OrdinalIgnoreCase))) return command;
        return command.Trim() + " --resume " + conversationId;
    }

    /// <summary>The running processes in this session's terminal (empty when it isn't running).</summary>
    public int[] ProcessIds() => process?.ProcessIds() ?? [];

    public void Resize(int cols, int rows) => process?.Resize(cols, rows);

    /// <summary>Kills the running process, if any. Does not raise <see cref="Exited"/>.</summary>
    /// <returns>False if nothing was running (it had already exited).</returns>
    public bool Stop()
    {
        var p = Interlocked.Exchange(ref process, null);
        p?.Dispose();
        return p != null;
    }

    public async Task InitGitAsync()
    {
        int generation;
        lock (gitLock)
        {
            if (disposed) return;
            generation = ++gitGeneration;
            watcher?.Dispose();
            watcher = null;
        }
        var root = await GitService.GetTopLevelAsync(Info.Directory);
        var external = root != null ? await GitService.GetExternalGitDirAsync(root) : null;
        lock (gitLock)
        {
            // A newer refresh started while git was answering, or the session was removed: installing a watcher (or
            // recording this answer's repository root) now would orphan the watcher, with its file watcher and 15 s poll
            // running for the rest of the app's life, and could replace the newer setup's root.
            if (disposed || generation != gitGeneration) return;
            RepoRoot = root;
            // Watch the session folder even if it isn't a repo yet, so a later `git init` is picked up.
            if (Directory.Exists(Info.Directory))
            {
                watcher = new GitWatcher(RepoRoot ?? Info.Directory, external);
                watcher.Changed += () => _ = RefreshStatusAsync();
            }
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
        lock (gitLock)
        {
            disposed = true;
            watcher?.Dispose();
            watcher = null;
        }
        Stop();
    }
}
