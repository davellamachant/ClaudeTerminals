using ClaudeTerminals.Git;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Sessions;

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

    /// <param name="claudeTheme">"light" or "dark": Claude is started in the theme that matches the terminal.</param>
    public void Start(int cols, int rows, string? claudeTheme = null)
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
            var command = PrepareCommand(Info.Command, ResumeOnce, () => ConversationIndex.HasConversation(Info.Directory), ThemeSettingsFile(claudeTheme));
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
    /// The command line for this start of a claude session:
    /// - after an update, the conversation it was in (<paramref name="resumeId"/>) replaces --continue, or is added to a
    ///   plain claude; a command that names its own conversation is left alone;
    /// - otherwise --continue is dropped when the folder has no saved conversation yet (<paramref name="hasConversation"/>),
    ///   since claude would stop with "No conversation found to continue". The saved command keeps it for next time.
    /// Anything that isn't an interactive claude is left alone.
    /// </summary>
    /// <param name="themeSettings">
    /// A settings file naming Claude's theme to match the app's terminals (Claude can't ask the terminal itself: the
    /// console layer swallows the question). Added unless the command already passes its own --settings.
    /// </param>
    internal static string PrepareCommand(string command, string? resumeId, Func<bool> hasConversation, string? themeSettings = null)
    {
        var tokens = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !(tokens[0].Equals("claude", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("claude.exe", StringComparison.OrdinalIgnoreCase)))
            return command;
        var flags = tokens.Skip(1).Select(t => t.Split('=')[0]).ToList();
        bool Has(params string[] names) => flags.Any(f => names.Contains(f, StringComparer.OrdinalIgnoreCase));
        if (Has("-p", "--print")) return command;   // no interface, so nothing to theme or continue

        var result = command;
        if (!Has("-r", "--resume", "--session-id", "--fork-session"))
        {
            if (resumeId != null && ConversationId().IsMatch(resumeId))
                result = WithoutContinue(command) + " --resume " + resumeId;
            else if (Has("-c", "--continue") && !hasConversation())
                result = WithoutContinue(command);
        }
        if (themeSettings != null && !Has("--settings"))
            result = result.TrimEnd() + " --settings \"" + themeSettings + "\"";
        return result;
    }

    /// <summary>The settings file that sets Claude's theme to "light" or "dark", written once into the app's data folder.</summary>
    internal static string? ThemeSettingsFile(string? theme)
    {
        if (theme is not ("light" or "dark")) return null;
        try
        {
            var path = Path.Combine(SessionStore.AppDataFolder, $"claude-theme-{theme}.json");
            var content = $$"""{"theme":"{{theme}}"}""";
            if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // Claude just uses its own theme setting
        }
    }

    /// <summary>Whether starting this command again carries on a conversation (--continue or --resume) rather than starting a new one.</summary>
    internal static bool CarriesOnConversation(string command)
    {
        var tokens = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !(tokens[0].Equals("claude", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("claude.exe", StringComparison.OrdinalIgnoreCase)))
            return false;
        string[] carryOn = ["-c", "--continue", "-r", "--resume"];
        return tokens.Skip(1).Any(t => carryOn.Contains(t.Split('=')[0], StringComparer.OrdinalIgnoreCase));
    }

    private static string WithoutContinue(string command) => ContinueFlag().Replace(command.Trim(), "");

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+(?:-c|--continue)(?=\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ContinueFlag();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9-]{1,100}$")]
    private static partial System.Text.RegularExpressions.Regex ConversationId();

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
