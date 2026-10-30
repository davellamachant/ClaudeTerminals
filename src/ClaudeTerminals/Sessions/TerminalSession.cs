using ClaudeTerminals.Git;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Sessions;

/// <summary>
/// How a session's agent reaches the app's panel server: Claude reads <paramref name="ConfigFile"/> (--mcp-config); Codex
/// is given <paramref name="Url"/>, and <paramref name="Token"/> through its environment.
/// </summary>
internal sealed record PanelLink(string? ConfigFile, string Url, string Token);

/// <summary>Runtime state for one configured session: its pseudo console process and its git watcher.</summary>
internal sealed partial class TerminalSession : IDisposable
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

    /// <summary>The repositories Git Changes can switch between (see <see cref="GitService.FindRepositoriesAsync"/>); <see cref="RepoRoot"/> is one of them.</summary>
    public IReadOnlyList<string> Repositories { get; private set; } = [];

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

    /// <summary>
    /// The prompt a new conversation starts with, once: set when a panel's button started this session. Claude gets
    /// it on its command line when that can be done exactly (an .exe); otherwise it is kept in <see cref="PromptToType"/>.
    /// </summary>
    public string? FirstPromptOnce { get; set; }

    /// <summary>A first prompt that couldn't go on the command line: typed into the terminal once Claude has connected to the app.</summary>
    public string? PromptToType { get; set; }

    /// <summary>When the current process started (UTC): a Codex conversation started after it is this session's (see <see cref="CodexConversations.Live"/>).</summary>
    public DateTime StartedUtc { get; private set; }

    /// <param name="theme">"light" or "dark": the agent is started in the theme that matches the terminal, if it can be.</param>
    /// <param name="panels">How the session's agent reaches the app's panel server, or null when panels are unavailable.</param>
    public void Start(int cols, int rows, string? theme = null, PanelLink? panels = null)
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
            var firstPrompt = FirstPromptOnce;
            var onCommandLine = firstPrompt != null && CommandLineResolver.RunsDirectly(Info.Command, env);
            var command = Info.Command;
            if (Agent is { } agent)
            {
                var launch = new Launch(Info.Directory, Info.Conversation, ResumeOnce, theme, panels,
                    NewConversation: firstPrompt != null, FirstPrompt: onCommandLine ? firstPrompt : null);
                command = agent.CommandLine(command, launch);
                agent.AddEnvironment(env, launch);
            }
            ResumeOnce = null;
            FirstPromptOnce = null;
            PromptToType = onCommandLine ? null : firstPrompt;
            StartedUtc = DateTime.UtcNow;
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


    /// <summary>The agent the session's command runs, or null for any other command.</summary>
    public AgentProfile? Agent => Agents.For(Info.Command);

    /// <summary>A conversation id the agents accept: a GUID.</summary>
    internal static bool IsConversationId(string? id) => id != null && Guid.TryParse(id, out _) && IsPlainId(id);

    /// <summary>Letters, digits and dashes only, so it can go on a command line as it is.</summary>
    internal static bool IsPlainId(string id) => PlainId().IsMatch(id);

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9-]{1,100}$")]
    private static partial System.Text.RegularExpressions.Regex PlainId();

    /// <summary>Whether the app can look after this command's conversation (see <see cref="AgentProfile.CanTrackConversation"/>).</summary>
    internal static bool CanTrackConversation(string command) => Agents.For(command)?.CanTrackConversation(command) == true;

    /// <summary>See <see cref="AgentProfile.TakeConversation"/>.</summary>
    internal static (string Command, string? Conversation) TakeConversation(string command) =>
        Agents.For(command)?.TakeConversation(command) ?? (command, null);

    /// <summary>See <see cref="AgentProfile.StripContinue"/>.</summary>
    internal static string StripContinue(string command) => Agents.For(command)?.StripContinue(command) ?? command;

    /// <summary>Whether this session carries on a conversation when it starts again, rather than starting a new one.</summary>
    internal static bool CarriesOnConversation(SessionInfo info) =>
        (IsConversationId(info.Conversation) && CanTrackConversation(info.Command)) || CarriesOnConversation(info.Command);

    /// <summary>Whether starting this command again carries on a conversation it picks itself (claude --continue, codex resume).</summary>
    internal static bool CarriesOnConversation(string command) => Agents.For(command)?.CarriesOnConversation(command) == true;

    /// <summary>
    /// Whether the app should give this session a conversation of its own: one whose agent lets the app choose the id
    /// (<see cref="AgentProfile.ChoosesConversationId"/>), without one, that doesn't carry on the folder's latest
    /// (claude --continue learns its conversation once Claude has picked it).
    /// </summary>
    internal static bool NeedsOwnConversation(SessionInfo info) =>
        info.Conversation == null && Agents.For(info.Command) is { ChoosesConversationId: true } agent &&
        agent.CanTrackConversation(info.Command) && !agent.CarriesOnConversation(info.Command);


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
        var repos = await GitService.FindRepositoriesAsync(Info.Directory);
        var root = ChooseRepository(repos, Info.GitRepo);
        var external = root != null ? await GitService.GetExternalGitDirAsync(root) : null;
        lock (gitLock)
        {
            // A newer refresh started while git was answering, or the session was removed: installing a watcher (or
            // recording this answer's repository root) now would orphan the watcher, with its file watcher and 15 s poll
            // running for the rest of the app's life, and could replace the newer setup's root.
            if (disposed || generation != gitGeneration) return;
            Repositories = repos;
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

    /// <summary>The picked repository while it is still there; otherwise the folder's own repository, or the first one inside it.</summary>
    internal static string? ChooseRepository(IReadOnlyList<string> repos, string? picked) =>
        repos.FirstOrDefault(r => string.Equals(r, picked, StringComparison.OrdinalIgnoreCase)) ?? repos.FirstOrDefault();

    /// <summary>Shows another of <see cref="Repositories"/> in Git Changes; false if it isn't one of them.</summary>
    public async Task<bool> SelectRepositoryAsync(string root)
    {
        var match = Repositories.FirstOrDefault(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase));
        if (match == null) return false;
        // The default needs no remembering, so a session whose folder later becomes a repository simply follows it.
        Info.GitRepo = string.Equals(match, Repositories[0], StringComparison.OrdinalIgnoreCase) ? null : match;
        await InitGitAsync();
        return true;
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
