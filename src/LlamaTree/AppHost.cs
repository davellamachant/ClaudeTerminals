using System.Text;
using System.Text.Json;
using LlamaTree.Git;
using LlamaTree.Panels;
using LlamaTree.Plugins;
using LlamaTree.Relay;
using LlamaTree.Sessions;
using LlamaTree.Terminal;
using LlamaTree.Updates;
using LlamaTree.Hosting;
using LlamaTree.Platform;

namespace LlamaTree;

/// <summary>
/// The app behind the page (xterm.js terminals + Monaco diffs): its sessions in pseudo terminals, git, panels and
/// plugins, bridged to the page through JSON messages. It runs in the session host (see <see cref="SessionHost"/>),
/// so sessions carry on while no window is open; the window that shows the page attaches to it (<see cref="IHostWindow"/>).
/// Everything here runs on the host's app thread.
/// </summary>
internal sealed partial class AppHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHostWindow window;
    private readonly Dictionary<string, TerminalSession> sessions = [];
    private readonly List<string> order = [];
    private readonly List<ProjectInfo> projects = ProjectStore.Load();
    private readonly Dictionary<string, StringBuilder> pendingOutput = [];
    /// <summary>Each session's recent output, shown by a window that attaches while it runs.</summary>
    private readonly Dictionary<string, OutputTail> tails = [];
    /// <summary>Sessions to redraw at their next resize: a window has just attached, and its terminals start empty.</summary>
    private readonly HashSet<string> repaint = [];
    private readonly System.Threading.Timer flushTimer;
    private int flushQueued;
    private volatile bool webReady;   // read by sessions' output threads too
    private bool shutDown;
    private UpdateInfo? update;
    /// <summary>
    /// The count of users (see <see cref="TelemetryService"/>): one request an hour, if you've said yes. Started when the
    /// page is first ready, it looks 20 seconds later (so it never competes with the start) and then every ten minutes.
    /// </summary>
    private readonly System.Threading.Timer countTimer;
    private bool countStarted;

    /// <summary>The window title: shown in the taskbar, Alt+Tab and the Dock.</summary>
    public static string Title => $"LlamaTree {UpdateService.CurrentVersion}";

    /// <summary>Whether any session's program is running.</summary>
    public bool AnyRunning => sessions.Values.Any(s => s.IsRunning);

    /// <summary>A session started or ended (on the app thread).</summary>
    public event Action? RunningChanged;

    /// <summary>The app should end: the sessions were stopped on the way out, or an update is about to install.</summary>
    public event Action? QuitRequested;

    /// <summary>
    /// How the window reached this host when it's on another computer (its SSH target), else null. Then nothing opens on
    /// this machine's desktop (no one is looking at it): a file opens on the window's computer, which is sent a copy,
    /// and a folder in the page's folder browser. This copy isn't updated from the window: a server updates its
    /// LlamaTree as it does its other packages.
    /// </summary>
    public string? Remote { get; set; }

    /// <summary>Files on their way from a window on another computer, pasted into a session.</summary>
    private readonly ServerFiles.Uploads uploads = new(ServerFiles.UploadFolder);

    /// <summary>Opens a folder: in the file manager, or for a window on another computer, in the page's folder browser.</summary>
    private void OpenFolder(string path)
    {
        if (Remote != null) Post(new { type = "files.browse", path });
        else Desktop.OpenExternal(path);
    }

    /// <summary>Shows a file or folder in the folder it's in.</summary>
    private void ShowInFolder(string path)
    {
        if (Remote != null) Post(new { type = "files.browse", path = Path.GetDirectoryName(path) ?? path, select = Path.GetFileName(path) });
        else Desktop.ShowInFolder(path);
    }

    /// <summary>Opens a file for viewing (never running it): here, or on a window's computer, which is sent a copy. Off the app thread.</summary>
    private void OpenFile(string path)
    {
        if (Remote != null) window.SendFile(path);
        else Desktop.OpenFileForViewing(path);
    }

    /// <summary>Each session's conversation, noted just before an update restarts the app (see <see cref="TerminalSession.ResumeOnce"/>).</summary>
    private static string ResumeFile => Path.Combine(SessionStore.AppDataFolder, "resume-after-update.json");

    public AppHost(IHostWindow window)
    {
        this.window = window;
        appThread = Environment.CurrentManagedThreadId;   // made on the host's app thread
        Terminal.SessionEnvironment.Warm();   // macOS, Linux: the login shell's environment, read while the page loads
        // Output is sent to the page in batches, at most 60 times a second.
        flushTimer = new System.Threading.Timer(_ =>
        {
            lock (pendingOutput) if (pendingOutput.Count == 0) return;
            if (Interlocked.Exchange(ref flushQueued, 1) == 0) RunOnUi(() => { flushQueued = 0; FlushOutput(); });
        }, null, 16, 16);
        conversationTimer = new System.Threading.Timer(_ => RunOnUi(FollowConversations), null, 10_000, 10_000);
        countTimer = new System.Threading.Timer(_ => RunOnUi(() => _ = TelemetryService.PingIfDueAsync(CountUsage())), null, Timeout.Infinite, Timeout.Infinite);

        var tidied = false;
        foreach (var info in SessionStore.Load())
        {
            // A session whose project no longer exists (e.g. projects.json was lost) just has no project.
            if (info.Project != null && !projects.Any(p => p.Id == info.Project)) info.Project = null;
            if (info.Server != null)
            {
                AddServerSession(info);   // run by its server's LlamaTree (see AppHost.Servers)
                continue;
            }
            tidied |= TidyConversation(info);
            AddSessionRuntime(info);
        }
        ApplyResumeAfterUpdate();
        foreach (var s in sessions.Values.Where(s => TerminalSession.NeedsOwnConversation(s.Info)))
        {
            tidied = true;
            if (GiveOwnConversation(s)) newConversations.Add(s.Id);
        }
        if (tidied) SaveSessions();
        StartPanels();
    }

    // ---------------------------------------------------------------- each session's own conversation

    /// <summary>
    /// A session keeps its conversation itself rather than in its command: "claude --resume X" becomes "claude" with
    /// conversation X (sessions saved before the app did this). An id that isn't a valid session id is dropped.
    /// </summary>
    private static bool TidyConversation(SessionInfo info)
    {
        if (info.Conversation != null && !TerminalSession.IsConversationId(info.Conversation)) { info.Conversation = null; return true; }
        if (info.Conversation != null) return false;
        var (command, conversation) = TerminalSession.TakeConversation(info.Command);
        if (conversation == null) return false;
        (info.Command, info.Conversation) = (command, conversation);
        return true;
    }

    /// <summary>Sessions that just got a new conversation in place of one the app never knew (told to the page once).</summary>
    private readonly List<string> newConversations = [];

    /// <summary>
    /// Gives a session saved before sessions had a conversation of its own (1.1.0) one now. Until then a plain claude
    /// started yet another new conversation each time the app started, under the session's old name, and the app only
    /// learned it once something was typed into it. An update from an older version noted the conversation it was in.
    /// </summary>
    /// <returns>True if it's a new conversation: the one the session was in isn't known.</returns>
    internal static bool GiveOwnConversation(TerminalSession s)
    {
        var noted = TerminalSession.IsConversationId(s.ResumeOnce) ? s.ResumeOnce : null;
        s.Info.Conversation = noted ?? Guid.NewGuid().ToString();
        return noted == null;
    }

    private readonly System.Threading.Timer conversationTimer;
    private bool following;

    /// <summary>
    /// Follows the conversation each running Claude is in, so the session reopens that one: it changes with /clear or
    /// /resume inside Claude, and a session started with --continue only learns its conversation once Claude has picked
    /// it. Only conversations Claude has saved count (an unsaved one can't be resumed).
    /// </summary>
    private async void FollowConversations()
    {
        if (following) return;
        var running = sessions.Values.Where(s => s.IsRunning && TerminalSession.CanTrackConversation(s.Info.Command))
            .Select(s => (Session: s, Others: OthersConversations(s))).ToList();
        if (running.Count == 0) return;
        following = true;
        try
        {
            // Off the window's thread: finding each session's processes reads the process table (on macOS, runs ps).
            var live = await Task.Run(() => running.Select(r => (r.Session, Id: r.Session.Agent?.LiveConversation(r.Session, r.Session.ProcessIds(), r.Others))).ToList());
            var changed = false;
            // Two sessions started together in one folder could both see the same new conversation (Codex's are told
            // apart only by folder and time): the first takes it.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (s, id) in live)
                if (id != null && seen.Add(id)) changed |= AdoptConversation(s, id);
            if (changed) { SaveSessions(); Post(SessionList()); }
        }
        finally
        {
            following = false;
        }
    }

    /// <summary>Makes <paramref name="live"/> the session's conversation (dropping --continue, which it no longer needs).</summary>
    private bool AdoptConversation(TerminalSession s, string? live)
    {
        if (!sessions.ContainsKey(s.Id) || live == null || live == s.Info.Conversation || !TerminalSession.CanTrackConversation(s.Info.Command)) return false;
        s.Info.Conversation = live;
        s.Info.Command = TerminalSession.StripContinue(s.Info.Command);
        return true;
    }

    /// <summary>Catches up on the session's conversation right now (before it's stopped or restarted).</summary>
    private void CatchUpConversation(TerminalSession s)
    {
        if (s.IsRunning && AdoptConversation(s, LiveConversation(s))) SaveSessions();
    }

    /// <summary>The saved conversation the running session's agent is in now, or null when that isn't known.</summary>
    private string? LiveConversation(TerminalSession s) => s.Agent?.LiveConversation(s, s.ProcessIds(), OthersConversations(s));

    /// <summary>The conversations the other sessions are in, which <paramref name="s"/> can't be in.</summary>
    private HashSet<string> OthersConversations(TerminalSession s) =>
        sessions.Values.Where(x => x != s).Select(x => x.Info.Conversation).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- panels

    private readonly PanelStore panelStore = new(Path.Combine(SessionStore.AppDataFolder, "panels.json"));

    /// <summary>Messages between sessions, through the panel server's tools; which session may message which is kept in relay.json.</summary>
    private readonly RelayHub relay = new(new RelayPolicy(Path.Combine(SessionStore.AppDataFolder, "relay.json")),
        path: Path.Combine(SessionStore.AppDataFolder, "relay-messages.json"));

    /// <summary>Your answers about plugins (see PluginGrants), kept in plugin-grants.json.</summary>
    private readonly PluginGrants grants = new(Path.Combine(SessionStore.AppDataFolder, "plugin-grants.json"));

    /// <summary>Sessions starting and driving others (start_session and the rest); what each may do is kept in control.json.</summary>
    private SessionControl? control;

    /// <summary>The host's app thread, where everything here runs (see ControlHost.OnUi).</summary>
    private readonly int appThread;

    private SessionControl Driving => control ??= new SessionControl(
        new ControlPolicy(Path.Combine(SessionStore.AppDataFolder, "control.json")), new ControlHost(this), () => relay.Policy.Paused);

    /// <summary>What may be done, what's being asked, and the log's version, as the page reads them.</summary>
    private object ControlState() => new
    {
        rules = Driving.Policy.Rules().Select(r => new { ability = r.Ability.ToString().ToLowerInvariant(), scope = r.Scope, decision = r.Decision.ToString().ToLowerInvariant() }),
        pending = Driving.Pending().Select(SpawnAsk),
        logVersion = Driving.LogVersion,
    };

    private static object SpawnAsk(SpawnRequest r) => new { id = r.Id, from = r.From, name = r.Name, directory = r.Directory, branch = r.Branch, prompt = r.Prompt };

    /// <summary>
    /// The app's sessions as <see cref="SessionControl"/> sees and changes them, here and on servers. It calls from the
    /// panel server's threads (a session here) or the app thread (a session on a server, see AppHost.Servers), and
    /// everything is done on the app thread.
    /// </summary>
    private sealed class ControlHost(AppHost app) : IControlHost
    {
        private T OnUi<T>(Func<T> f)
        {
            if (Environment.CurrentManagedThreadId == app.appThread) return f();
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            app.RunOnUi(() =>
            {
                try { done.SetResult(f()); }
                catch (Exception ex) { done.SetException(ex); }
            });
            return done.Task.GetAwaiter().GetResult();
        }

        private void OnUi(Action a) => OnUi(() => { a(); return 0; });

        private Task<T> OnUiAsync<T>(Func<Task<T>> f) => OnUi(f);

        public IReadOnlyList<ControlSession> Sessions() => OnUi(() => app.order.Select(id => app.sessions.TryGetValue(id, out var s)
            ? new ControlSession(s.Id, s.Info.Name.Length > 0 ? s.Info.Name : s.Id, s.Info.Directory, s.Info.Project, s.Info.StartedBy, s.Agent != null, s.IsRunning, null)
            : app.serverSessions[id] is var x
                ? new ControlSession(x.Info.Id, x.Info.Name.Length > 0 ? x.Info.Name : x.Info.Id, x.Info.Directory, x.Info.Project, x.Info.StartedBy, Agents.For(x.Info.Command) != null, x.Running, x.Info.Server)
                : null!).ToList());

        /// <summary>The starter's own agent and options, never a command of the agent's choosing, in a conversation of its own.</summary>
        private static string CommandLike(string command) => TerminalSession.StripContinue(TerminalSession.TakeConversation(command).Command);

        public Task<string> StartAsync(string parent, string name, string directory, string prompt, string? within) => OnUiAsync(async () =>
        {
            if (app.serverSessions.TryGetValue(parent, out var sp))
            {
                // A session on a server starts one on the same server: its folders are there.
                // The server checks the folder is really inside the allowed one (within), links resolved there; one too old
                // to check isn't asked to start it there at all.
                var c = app.ServerFor(sp.Info.Server!);
                await c.WhenSettledAsync(TimeSpan.FromSeconds(30));
                if (within != null && c.IsOnline && !c.Supports("confined-start"))
                    throw new ControlException($"LlamaTree on {c.Target} is too old to check where a new session's folder leads, so a session can't be started there. Update it first (the server's badge or its sessions' menu).");
                var before = app.serverSessions.Keys.ToHashSet();
                app.addingTo[c.Target] = sp.Info.Project;
                JsonElement made;
                try
                {
                    await c.WhenSettledAsync(TimeSpan.FromSeconds(30));
                    made = await c.RequestAsync(new { type = "addSessions", sessions = new[] { new { directory, name, command = CommandLike(sp.Info.Command), prompt, within } } });
                }
                catch (InvalidOperationException ex) { throw new ControlException(ex.Message); }
                finally { app.addingTo.Remove(c.Target); }
                // The session the server says it made (it's in its session list by now); a server from before it said
                // so: the one new session of it here.
                var added = StartedOnServer(made, rid => app.FindServerSession(c.Target, rid),
                    app.serverSessions.Values.Where(x => !before.Contains(x.Info.Id) && x.Info.Server == sp.Info.Server));
                if (added == null) throw new ControlException($"{c.Target} didn't add the session.");
                added.Info.StartedBy = parent;
                app.SaveSessions();
                app.Post(app.SessionList());
                app.Post(new { type = "control.start", id = added.Info.Id });   // the page starts it, at its tile's size
                return added.Info.Id;
            }
            var p = app.sessions.GetValueOrDefault(parent) ?? throw new ControlException("That session isn't in LlamaTree any more.");
            if (!Directory.Exists(directory)) throw new ControlException($"{directory} doesn't exist.");
            // Every session an agent starts is held to a folder: the one it was allowed, resolved once here, the folder
            // checked against that very value, which is what's kept (and checked again as its process starts).
            var confined = PathText.Confine(directory, within ?? throw new ControlException("A started session needs the folder it's allowed."))
                ?? throw new ControlException($"{directory} isn't inside {within} (a folder on the way leads elsewhere, or isn't there), so a session can't be started there.");
            var info = new SessionInfo
            {
                Name = name, Directory = confined.Folder, Command = CommandLike(p.Info.Command), Project = p.Info.Project, StartedBy = parent,
                Within = confined.Root,
            };
            if (TerminalSession.NeedsOwnConversation(info)) info.Conversation = Guid.NewGuid().ToString();
            var session = app.AddSessionRuntime(info);
            session.FirstPromptOnce = prompt;
            app.SaveSessions();
            app.Post(app.SessionList());
            app.Post(new { type = "control.start", id = info.Id });   // the page starts it, at its tile's size
            return info.Id;
        });

        public Task<string> AddWorktreeAsync(string parent, string branch) => OnUiAsync(async () =>
        {
            if (app.serverSessions.TryGetValue(parent, out var sp)) return await ServerWorktreeAsync(sp, branch);
            var p = app.sessions.GetValueOrDefault(parent) ?? throw new ControlException("That session isn't in LlamaTree any more.");
            var root = await GitService.GetTopLevelAsync(p.RepoRoot ?? p.Info.Directory)
                ?? throw new ControlException("This session's folder isn't in a Git repository, so there's no worktree to make; give a folder instead.");
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var slug = System.Text.RegularExpressions.Regex.Replace(branch, "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
            var path = $"{root}-{(slug.Length > 0 ? slug : "worktree")}";
            for (var i = 2; Directory.Exists(path) || File.Exists(path); i++) path = $"{root}-{slug}-{i}";
            var result = await GitService.AddWorktreeAsync(root, path, branch, null);
            if (!result.Ok) throw new ControlException(string.IsNullOrWhiteSpace(result.Error) ? "git couldn't make the worktree." : result.Error.Trim());
            return path;
        });

        /// <summary>
        /// A new worktree of a server session's repository, beside it on the server (its folder's name with the branch's
        /// after it, numbered if that's taken), made by the server's LlamaTree as for its own sessions.
        /// </summary>
        private async Task<string> ServerWorktreeAsync(ServerSession sp, string branch)
        {
            var root = sp.RepoRoot?.TrimEnd('/')
                ?? throw new ControlException("This session's folder isn't in a Git repository (or its server hasn't said yet), so there's no worktree to make; give a folder instead.");
            var c = app.ServerFor(sp.Info.Server!);
            var slug = System.Text.RegularExpressions.Regex.Replace(branch, "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
            var name = root[(root.LastIndexOf('/') + 1)..] + "-" + (slug.Length > 0 ? slug : "worktree");
            var parentDir = root[..Math.Max(root.LastIndexOf('/'), 1)];
            try
            {
                // What's already beside it there, to pick a free name.
                var listing = await c.RequestAsync(new { type = "fs.list", path = parentDir });
                var taken = listing.TryGetProperty("entries", out var entries)
                    ? entries.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal) : [];
                var pick = name;
                for (var i = 2; taken.Contains(pick); i++) pick = $"{name}-{i}";
                var path = parentDir.TrimEnd('/') + "/" + pick;
                var made = await c.RequestAsync(new { type = "worktree.add", id = sp.Info.RemoteId, branch, path, @base = (string?)null });
                return made.ValueKind == JsonValueKind.String ? made.GetString()! : path;
            }
            catch (InvalidOperationException ex) { throw new ControlException(ex.Message); }
        }

        // Typed by the page, as you would: a session on a server gets it there.
        public void TypePrompt(string id, string text) => app.RunOnUi(() => app.Post(new { type = "control.prompt", id, text }));

        public void SendKeys(string id, string keys) => app.RunOnUi(() =>
        {
            if (app.sessions.GetValueOrDefault(id) is { } s) s.Write(keys);
            else if (app.serverSessions.GetValueOrDefault(id) is { } x && app.servers.GetValueOrDefault(x.Info.Server!) is { IsOnline: true } c)
                c.Send(JsonSerializer.Serialize(new { type = "input", id = x.Info.RemoteId, data = keys }, JsonOptions));
        });

        public void Stop(string id) => app.RunOnUi(() =>
        {
            if (app.serverSessions.GetValueOrDefault(id) is { } x)
            {
                if (app.servers.GetValueOrDefault(x.Info.Server!) is { IsOnline: true } c)
                    _ = c.RequestAsync(new { type = "stop", id = x.Info.RemoteId }).ContinueWith(_ => { }, TaskScheduler.Default);
                return;
            }
            if (app.sessions.GetValueOrDefault(id) is not { } s) return;
            app.CatchUpConversation(s);
            if (!s.Stop()) return;
            app.FlushOutput();
            app.Post(new { type = "exited", id, code = (int?)null });
            app.RunningChanged?.Invoke();
        });

        public void Remove(string id) => OnUi(() =>
        {
            if (app.sessions.GetValueOrDefault(id) is { } s)
            {
                app.RemoveSession(s);
                return;
            }
            if (app.serverSessions.GetValueOrDefault(id) is not { } x) return;
            // Removed on its server too, as when you close it (one that can't be reached keeps it, and shows it again).
            if (app.servers.GetValueOrDefault(x.Info.Server!) is { IsOnline: true } c)
                _ = c.RequestAsync(new { type = "removeSession", id = x.Info.RemoteId }).ContinueWith(_ => { }, TaskScheduler.Default);
            app.ForgetServerSession(x);
            app.ForgetPanels(id);
            app.SaveSessions();
            app.Post(app.SessionList());
        });

        public SessionReport? Report(string id)
        {
            // A session on a server: its conversation is there (what it shows comes through its terminal and status).
            // The session's process is read on the app thread, as everywhere; its conversation file off it.
            var (s, running, pids) = OnUi(() => app.sessions.GetValueOrDefault(id) is { } x ? ((TerminalSession?)x, x.IsRunning, (int[]?)x.ProcessIds()) : (null, false, null));
            var agent = s?.Agent;
            if (s == null || agent == null) return null;
            var conv = (running && pids != null ? agent.ReadConversation(s, pids) : null) ?? agent.ReadSavedConversation(s);
            if (conv?.Turns.LastOrDefault() is not { } turn) return null;
            var said = turn.Items.Where(i => i.Kind == "text").Select(i => i.Text).ToList();
            var last = turn.Items.LastOrDefault();
            return new SessionReport(turn.Prompt, said.Count > 0 ? string.Join("\n\n", said) : null,
                last?.Kind == "tool" ? $"{last.Tool}: {last.Text}" : null);
        }
    }

    private List<RelayPeer> RelayPeers()
    {
        // A session counts in a project only while that project exists (as the page shows it), for plugins' projects too.
        string? ProjectOf(SessionInfo s) => projects.Any(p => p.Id == s.Project) ? s.Project : null;
        var list = order.Where(sessions.ContainsKey).Select(id => sessions[id]).Where(s => s.Agent != null).Select(s => new RelayPeer(
            s.Id, s.Info.Name.Length > 0 ? s.Info.Name : s.Id, ProjectOf(s.Info), projects.FirstOrDefault(p => p.Id == s.Info.Project)?.Name,
            s.Agent?.Name, s.IsRunning)).ToList();
        // Sessions on servers too: their tool calls are answered here while their server is connected (AppHost.Servers).
        list.AddRange(order.Where(serverSessions.ContainsKey).Select(id => serverSessions[id]).Where(x => Agents.For(x.Info.Command) != null).Select(x => new RelayPeer(
            x.Info.Id, x.Info.Name.Length > 0 ? x.Info.Name : x.Info.Id, ProjectOf(x.Info), projects.FirstOrDefault(p => p.Id == x.Info.Project)?.Name,
            Agents.For(x.Info.Command)?.Name, x.Running)));
        // Plugins allowed to message sessions take part too, as "plugin:<id>" (see PluginGrants.RelayGate).
        list.AddRange(plugins.Values.Where(p => p.Access.Contains("message") && grants.State(p.Id, "message") == "allow")
            .Select(p => new RelayPeer(PluginGrants.PeerPrefix + p.Id, p.Title, null, null, "Plugin", true, p.Inbox)));
        return list;
    }

    /// <summary>Your answers about plugins, as the page reads them.</summary>
    private object GrantsMessage() => new
    {
        type = "plugins.grants",
        grants = grants.Snapshot().ToDictionary(x => x.Key, x => new
        {
            allowed = x.Value.Allowed, denied = x.Value.Denied, projects = x.Value.Projects, hidden = x.Value.Hidden, changed = x.Value.Changed,
        }),
    };

    /// <summary>The rules, whether messages are paused, what's waiting for your answer, and each session's unread count.</summary>
    private object RelayState() => new
    {
        paused = relay.Policy.Paused,
        rules = relay.Policy.Rules().Select(r => new { from = r.From, to = r.To, decision = r.Decision.ToString().ToLowerInvariant() }),
        waiting = relay.Waiting().Select(w => new { from = w.From, to = w.To, count = w.Count }),
        unread = sessions.Keys.Select(id => (id, n: relay.Unread(id))).Where(x => x.n > 0).ToDictionary(x => x.id, x => x.n),
        logVersion = relay.LogVersion,
    };

    private static RelayDecision ParseDecision(string s) => s switch
    {
        "allow" => RelayDecision.Allow,
        "deny" => RelayDecision.Deny,
        "ask" => RelayDecision.Ask,
        _ => throw new ArgumentException($"There's no decision \"{s}\"."),
    };
    private PanelServer? panelServer;
    private PanelMcp? mcp;   // the tools sessions call (panels, messages), here and on servers (see AppHost.Servers)

    private static string PanelConfigFolder => Path.Combine(SessionStore.AppDataFolder, "panels");

    private static string PluginFolder => Path.Combine(SessionStore.AppDataFolder, "plugins");

    /// <summary>Themes of your own (see <see cref="Themes.ThemeCatalog"/>); the app's own are in wwwroot/themes.</summary>
    private static string ThemeFolder => Path.Combine(SessionStore.AppDataFolder, "themes");
    private static string BuiltInThemeFolder => Path.Combine(AppContext.BaseDirectory, "wwwroot", "themes");
    private readonly Dictionary<string, PluginInfo> plugins = [];

    /// <summary>
    /// Reads the plugins folder and gives each plugin its address, where the window serves its folder (see
    /// <see cref="IHostWindow.ServePlugin"/>).
    /// </summary>
    private (List<PluginInfo> Found, List<string> Problems) LoadPlugins()
    {
        var (found, problems) = PluginCatalog.Load(PluginFolder);
        AddServerPlugins(found, problems);   // and connected servers' (see AppHost.Servers)
        foreach (var gone in plugins.Keys.Except(found.Select(p => p.Id)).ToList())
        {
            window.StopServingPlugin(gone);
            plugins.Remove(gone);
        }
        foreach (var p in found)
        {
            if (!plugins.TryGetValue(p.Id, out var had) || had.Folder != p.Folder)
                window.ServePlugin(p);
            plugins[p.Id] = p;
        }
        return (found, problems);
    }

    private void StartPanels()
    {
        panelStore.KeepOwners(order);
        panelStore.Changed += change => RunOnUi(() => Post(PanelsMessage(change)));
        relay.Policy.KeepSessions(order);
        relay.Gate = grants.RelayGate;
        relay.SetPeers(RelayPeers());
        // Answers about plugins change which plugins take part in messages, so the peers are read again too.
        grants.Changed += () => RunOnUi(() => { relay.SetPeers(RelayPeers()); Post(GrantsMessage()); });
        // The state first, so the page has the message waiting by the time it asks you about it.
        relay.Asked += ask => RunOnUi(() => { Post(new { type = "relay", state = RelayState() }); Post(new { type = "relay.ask", from = ask.From, to = ask.To }); });
        relay.Delivered += m => RunOnUi(() => Post(new { type = "relay.message", to = m.To, from = m.From, unread = relay.Unread(m.To) }));
        relay.Changed += () => RunOnUi(() => Post(new { type = "relay", state = RelayState() }));
        relay.Policy.Changed += () => RunOnUi(() => Post(new { type = "relay", state = RelayState() }));
        Driving.Policy.Keep(order, projects.Select(p => p.Id));
        Driving.Asked += r => RunOnUi(() => Post(new { type = "control.ask", request = SpawnAsk(r) }));
        Driving.Settled += id => RunOnUi(() => Post(new { type = "control.settled", id }));
        Driving.Changed += () => RunOnUi(() => Post(new { type = "control", state = ControlState() }));
        Driving.Policy.Changed += () => RunOnUi(() => Post(new { type = "control", state = ControlState() }));
        mcp = new PanelMcp(panelStore, relay, Driving);
        // A first prompt that couldn't go on the command line is typed once Claude is up (it connects as it starts).
        mcp.Initialized += id => RunOnUi(async () =>
        {
            Post(new { type = "agent.ready", id });   // the page tells a session it woke of its messages once it's up
            if (!sessions.TryGetValue(id, out var s) || s.PromptToType is not { } prompt) return;
            s.PromptToType = null;
            await Task.Delay(1500);
            Post(new { type = "typePrompt", id, text = prompt });
        });
        try { panelServer = PanelServer.TryStart(mcp); }
        catch (Exception) { /* sessions start without panels */ }
        // Shown among another computer's sessions: its window answers the sessions' tool calls while it's attached.
        if (panelServer != null) panelServer.ForwardToolCall = (session, m) => Remote != null ? window.ForwardToolCall(session, m) : null;
    }

    private object PanelsMessage(PanelChange? change = null) => new
    {
        type = "panels",
        panels = panelStore.Snapshot(),
        closed = panelStore.Closed().Select(p => new { owner = p.Owner, id = p.Id, title = p.Title }),
        changed = change?.Id == null ? null : new { owner = change.Owner, id = change.Id, created = change.Created },
    };

    /// <summary>
    /// How the session reaches the panel server (this run's port and the session's token), writing Claude's --mcp-config
    /// file; null if panels are unavailable.
    /// </summary>
    private PanelLink? PanelLinkFor(TerminalSession s)
    {
        if (panelServer == null) return null;
        var link = new PanelLink(null, panelServer.Url, panelServer.TokenFor(s.Id));
        if (s.Agent?.PanelsFromFile != true) return link;   // configured on its command line, if at all
        try
        {
            Directory.CreateDirectory(PanelConfigFolder);
            var path = Path.Combine(PanelConfigFolder, $"mcp-{s.Id}.json");
            File.WriteAllText(path, panelServer.ConfigJson(s.Id));
            return link with { ConfigFile = path };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ForgetPanels(string sessionId)
    {
        panelServer?.Forget(sessionId);
        panelStore.KeepOwners(order);
        relay.Policy.KeepSessions(order);
        Driving.Policy.Keep(order, projects.Select(p => p.Id));
        Driving.Forget(sessionId);
        try { File.Delete(Path.Combine(PanelConfigFolder, $"mcp-{sessionId}.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>After an update, sessions carry on in the conversations they were in (read once, then deleted).</summary>
    private void ApplyResumeAfterUpdate()
    {
        try
        {
            var file = new FileInfo(ResumeFile);
            if (!file.Exists) return;
            var fresh = DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMinutes(30);   // an old note is stale
            var map = fresh ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file.FullName)) : null;
            file.Delete();
            foreach (var (id, conversation) in map ?? [])
                if (sessions.TryGetValue(id, out var s)) s.ResumeOnce = conversation;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Sessions just start as they normally would.
        }
    }

    /// <summary>
    /// The accent colour and app mode, as a message for the page (Windows reports both; elsewhere the page follows the
    /// system's light or dark). The window sends it too, when Windows says they've changed.
    /// </summary>
    public static string AccentMessage() =>
        JsonSerializer.Serialize(new { type = "accent", palette = AccentColors.Read(), lightApps = AccentColors.AppsUseLightTheme() }, JsonOptions);

    /// <summary>A window has attached: it serves the plugins again, and shows the sessions once its page says it's ready.</summary>
    public void Attached()
    {
        webReady = false;
        foreach (var p in plugins.Values) window.ServePlugin(p);
    }

    /// <summary>The window has gone: sessions run on, their output kept in <see cref="tails"/> for the next one.</summary>
    public void Detached()
    {
        webReady = false;
        lock (pendingOutput) pendingOutput.Clear();
        uploads.AbandonAll();
    }

    /// <summary>The app is ending: stop the sessions, each noting the conversation it's in so it reopens there. Once only.</summary>
    public void Shutdown()
    {
        if (shutDown) return;
        shutDown = true;
        flushTimer.Dispose();
        conversationTimer.Dispose();
        countTimer.Dispose();
        foreach (var s in sessions.Values) CatchUpConversation(s);   // so each reopens where it was
        foreach (var s in sessions.Values) s.Dispose();
        panelServer?.Dispose();
        DisposeServers();
        relay.Flush();   // unread and waiting messages, for next time
    }

    /// <summary>
    /// What closing the window asks when sessions are running: keep them running without the window, or stop them. Stopping
    /// loses nothing: Claude keeps every conversation and the sessions come back with the app (a Claude session starts
    /// when it's first opened), so say that (and which of them carry on their conversation) rather than that they'll be
    /// "terminated".
    /// </summary>
    internal static string CloseMessage(IReadOnlyCollection<TerminalSession> running)
    {
        var n = running.Count;
        var carryOn = running.Count(s => TerminalSession.CarriesOnConversation(s.Info));
        var fresh = n - carryOn;
        var sessionsAre = n == 1 ? "1 session is" : $"{n} sessions are";
        var restart = (carryOn, fresh) switch
        {
            (_, 0) => n == 1
                ? "It comes back the next time you open LlamaTree and picks up where it left off."
                : "They come back the next time you open LlamaTree and pick up where they left off.",
            (0, _) => n == 1
                ? "It comes back the next time you open LlamaTree, with a new conversation (the current one can be reopened with \"Resume a different conversation…\")."
                : "They come back the next time you open LlamaTree, with new conversations (the current ones can be reopened with \"Resume a different conversation…\").",
            _ => $"They come back the next time you open LlamaTree: {carryOn} pick{(carryOn == 1 ? "s" : "")} up where {(carryOn == 1 ? "it" : "they")} left off, " +
                 $"{fresh} start{(fresh == 1 ? "s" : "")} a new conversation (the current one can be reopened with \"Resume a different conversation…\").",
        };
        return $"{sessionsAre} running.\n\n" +
               $"Kept running, {(n == 1 ? "it carries" : "they carry")} on without the window and {(n == 1 ? "is" : "are")} here when you open LlamaTree again.\n\n" +
               $"Stopping {(n == 1 ? "it" : "them")} loses nothing: Claude keeps every conversation. {restart} Anything Claude is in the middle of will be interrupted.";
    }

    // ---------------------------------------------------------------- sessions

    private TerminalSession AddSessionRuntime(SessionInfo info)
    {
        var s = new TerminalSession(info);
        var tail = tails[info.Id] = new OutputTail();
        s.Output += (session, text) =>
        {
            tail.Append(text);
            lock (pendingOutput)
            {
                if (!webReady) return;   // no page to send it to: the tail keeps it for the next window
                if (!pendingOutput.TryGetValue(session.Id, out var sb)) pendingOutput[session.Id] = sb = new StringBuilder();
                sb.Append(text);
            }
        };
        s.Exited += (session, code, generation) => RunOnUi(() =>
        {
            // A process that exited just as the session was restarted must not mark the new one as exited. Both this
            // check and restart run on the UI thread, so the comparison can't race a Start.
            if (generation != session.Generation) return;
            FlushOutput();
            Post(new { type = "exited", id = session.Id, code });
            RunningChanged?.Invoke();
        });
        s.StatusChanged += (session, status) => RunOnUi(() => Post(StatusMessage(session, status)));
        sessions[info.Id] = s;
        order.Add(info.Id);
        _ = s.InitGitAsync();
        return s;
    }

    /// <summary>Removes a session here: its process ends, its panels go with it (its conversation stays, to resume).</summary>
    private void RemoveSession(TerminalSession s)
    {
        CatchUpConversation(s);
        s.Dispose();
        sessions.Remove(s.Id);
        order.Remove(s.Id);
        tails.Remove(s.Id);
        repaint.Remove(s.Id);
        RunningChanged?.Invoke();
        SaveSessions();
        ForgetPanels(s.Id);
        Post(SessionList());
    }

    private void SaveSessions() => SessionStore.Save(order.Select(id => sessions.TryGetValue(id, out var s) ? s.Info : serverSessions[id].Info));

    private object SessionList()
    {
        relay.SetPeers(RelayPeers());   // the list goes out whenever sessions or projects change
        grants.Keep(order.ToHashSet(), projects.Select(p => p.Id).ToHashSet());
        return SessionListMessage();
    }

    private object SessionListMessage() => new
    {
        type = "sessions",
        agents = Agents.All.Select(a => a.Describe()),
        projects = projects.Select(p => new { id = p.Id, name = p.Name, color = p.Color }),
        sessions = order.Select(id => sessions.TryGetValue(id, out var s) ? (s.Info, s.IsRunning) : (serverSessions[id].Info, serverSessions[id].Running)).Select(x => new
        {
            id = x.Info.Id,
            name = x.Info.Name,
            directory = x.Info.Directory,
            command = x.Info.Command,
            project = x.Info.Project,
            conversation = x.Info.Conversation,
            running = x.Item2,
            server = x.Info.Server,
            startedBy = x.Info.StartedBy,
        }),
        servers = ServersState(),
    };

    /// <summary>A project id from the page, or null for "no project" (empty or unknown ids included).</summary>
    private string? ProjectId(JsonElement e, string name = "project") =>
        OptStr(e, name) is { Length: > 0 } id && projects.Any(p => p.Id == id) ? id : null;

    private static string ProjectName(JsonElement msg) =>
        Str(msg, "name").Trim() is { Length: > 0 } name ? (name.Length > 60 ? name[..60] : name)
            : throw new ArgumentException("A project needs a name.");

    private static object StatusMessage(TerminalSession s, GitStatus st) => new
    {
        type = "git.status",
        id = s.Id,
        isRepo = st.IsRepo,
        error = st.Error,
        root = s.RepoRoot,
        repos = s.Repositories,
        head = st.HeadId,
        branch = st.Branch,
        upstream = st.Upstream,
        ahead = st.Ahead,
        behind = st.Behind,
        staged = st.Staged,
        unstaged = st.Unstaged,
    };

    private void FlushOutput()
    {
        if (!webReady) return;
        List<(string Id, string Data)> batch;
        lock (pendingOutput)
        {
            if (pendingOutput.Count == 0) return;
            batch = pendingOutput.Where(kv => kv.Value.Length > 0).Select(kv => (kv.Key, kv.Value.ToString())).ToList();
            pendingOutput.Clear();
        }
        foreach (var (id, data) in batch) Post(new { type = "output", id, data });
    }

    // ---------------------------------------------------------------- bridge

    private void Post(object message) => window.PostJson(JsonSerializer.Serialize(message, JsonOptions));

    /// <summary>Runs <paramref name="action"/> on the UI thread, later (never inline); dropped once the window has closed.</summary>
    private void RunOnUi(Action action) => window.RunOnUi(action);

    /// <summary>A message from the app page, which the window has made sure came from it (and not from a plugin's frame).</summary>
    public async void Receive(string json)
    {
        JsonElement msg;
        try
        {
            msg = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        var type = Str(msg, "type");
        var reqId = msg.TryGetProperty("reqId", out var r) ? r.GetInt32() : (int?)null;
        try
        {
            var result = await HandleAsync(type, msg);
            if (reqId != null) Post(new { type = "reply", reqId, ok = true, result });
        }
        catch (Exception ex)
        {
            if (reqId != null) Post(new { type = "reply", reqId, ok = false, error = ex.Message });
        }
    }

    private async Task<object?> HandleAsync(string type, JsonElement msg)
    {
        if (await TryForwardAsync(type, msg) is (true, var forwarded)) return forwarded;
        switch (type)
        {
            case "ready":
                window.PostJson(AccentMessage());   // first: with Windows' app mode the page knows its theme before sessions start
                Post(SessionList());
                // Sessions that ran on while no window was open: what they showed, and a redraw at the page's size.
                foreach (var s in order.Where(sessions.ContainsKey).Select(id => sessions[id]).Where(s => s.IsRunning))
                {
                    if (tails[s.Id].ToString() is { Length: > 0 } shown) Post(new { type = "replay", id = s.Id, data = shown });
                    repaint.Add(s.Id);
                }
                webReady = true;   // after the replay, so live output follows it
                ServersPageReady();
                Post(PanelsMessage());
                if (newConversations.Count > 0)
                {
                    Post(new { type = "newConversations", ids = newConversations });
                    newConversations.Clear();
                }
                foreach (var s in sessions.Values) _ = s.RefreshStatusAsync();
                if (!countStarted)
                {
                    countStarted = true;
                    countTimer.Change(TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(10));
                }
                return null;

            case "start":
            {
                var s = Get(msg);
                tails[s.Id].Clear();
                var prompted = s.FirstPromptOnce != null;   // starts on a prompt: its first output is work, not start-up
                s.Start(Int(msg, "cols"), Int(msg, "rows"), OptStr(msg, "theme"), PanelLinkFor(s));
                RunningChanged?.Invoke();
                return new { prompted };
            }
            case "input":
                Get(msg).Write(Str(msg, "data"));
                return null;

            case "resize":
            {
                var s = Get(msg);
                int cols = Int(msg, "cols"), rows = Int(msg, "rows");
                if (repaint.Remove(s.Id) && rows > 2)
                {
                    // A terminal told the size it already has hears nothing, so a program that ran on without a window
                    // is shrunk by a row and given its size back: it redraws its whole screen in the new terminal.
                    s.Resize(cols, rows - 1);
                    await Task.Delay(80);
                }
                s.Resize(cols, rows);
                return null;
            }

            case "restart":
            {
                var s = Get(msg);
                CatchUpConversation(s);   // a session with its own conversation restarts in the one it's in now
                // Restarting to pick up a new theme: carry on in the exact conversation the session is in.
                if (Bool(msg, "resume") && s.IsRunning && LiveConversation(s) is { } live)
                    s.ResumeOnce = live;
                s.Stop();
                tails[s.Id].Clear();
                var prompted = s.FirstPromptOnce != null;
                s.Start(Int(msg, "cols"), Int(msg, "rows"), OptStr(msg, "theme"), PanelLinkFor(s));
                RunningChanged?.Invoke();
                return new { prompted };
            }
            case "app.closeInfo":
            {
                // Asked by the page when the window is closing, before it asks you whether to keep sessions running.
                var running = sessions.Values.Where(s => s.IsRunning).ToList();
                return new { running = running.Count, message = running.Count > 0 ? CloseMessage(running) : null };
            }
            case "app.close":
            {
                // Your answer: the window closes either way; stopping the sessions also ends the app.
                var stop = Bool(msg, "stop");
                RunOnUi(() =>
                {
                    window.Close();
                    if (stop) QuitRequested?.Invoke();
                });
                return null;
            }
            case "stop":
            {
                // Stopping disposes the process without raising Exited, so tell the page itself (code null = stopped).
                var s = Get(msg);
                CatchUpConversation(s);
                if (s.Stop())
                {
                    FlushOutput();
                    Post(new { type = "exited", id = s.Id, code = (int?)null });
                    RunningChanged?.Invoke();
                }
                return null;
            }

            case "addSessions":
            {
                var items = msg.GetProperty("sessions").EnumerateArray()
                    .Select(item => (Dir: Str(item, "directory"), Name: Str(item, "name"), Command: Str(item, "command"), Project: ProjectId(item),
                        Prompt: OptStr(item, "prompt")?.Trim() is { Length: > 0 and <= PanelSpec.MaxPrompt } p ? p : null,
                        Conversation: OptStr(item, "conversation") is { } conv && TerminalSession.IsConversationId(conv) ? conv : null,
                        Within: OptStr(item, "within")))
                    .ToList();
                // All or nothing: check every folder before adding any. Adding as we went left the earlier sessions
                // added but unsaved and unshown after a failure, and a retry then added them a second time.
                for (var i = 0; i < items.Count; i++)
                {
                    if (!Directory.Exists(items[i].Dir)) throw new DirectoryNotFoundException(items[i].Dir);
                    // Started by another session (start_session on another computer): it must really be inside the
                    // folder it was allowed, links resolved here, where they lead; that folder is resolved once, and the
                    // value checked against is the one kept (see PathText.Confine).
                    if (items[i].Within is { } within)
                    {
                        var confined = PathText.Confine(items[i].Dir, within)
                            ?? throw new InvalidOperationException($"{items[i].Dir} isn't inside {within} (a folder on the way leads elsewhere, or isn't there), so a session can't be started there.");
                        items[i] = items[i] with { Dir = confined.Folder, Within = confined.Root };
                    }
                }
                var made = new List<string>();
                foreach (var item in items)
                {
                    var info = new SessionInfo
                    {
                        Name = string.IsNullOrWhiteSpace(item.Name) ? Path.GetFileName(PathText.TrimEnd(item.Dir)) : item.Name.Trim(),
                        Directory = item.Dir,
                        Command = string.IsNullOrWhiteSpace(item.Command) ? "claude" : item.Command.Trim(),
                        Project = item.Project,
                        Within = item.Within,   // as resolved and checked above; checked again at each start
                    };
                    TidyConversation(info);   // a "claude --resume X" typed as the command: conversation X
                    if (item.Conversation != null && TerminalSession.CanTrackConversation(info.Command))
                    {
                        info.Conversation = item.Conversation;   // picked in the resume picker
                        info.Command = TerminalSession.StripContinue(info.Command);
                    }
                    // A new Claude session gets a conversation of its own from the start, so it always reopens that one,
                    // even with other sessions in the same folder. One that should carry on the folder's latest
                    // (--continue) learns its conversation once Claude has picked it.
                    if (TerminalSession.NeedsOwnConversation(info)) info.Conversation = Guid.NewGuid().ToString();
                    var added = AddSessionRuntime(info);
                    added.FirstPromptOnce = item.Prompt;   // from a panel's button: its first start is a new conversation with this prompt
                    made.Add(info.Id);
                }
                SaveSessions();
                Post(SessionList());
                return made;
            }
            case "updateSession":
            {
                var s = Get(msg);
                if (msg.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString())) s.Info.Name = n.GetString()!.Trim();
                if (msg.TryGetProperty("command", out var c) && !string.IsNullOrWhiteSpace(c.GetString()))
                {
                    // A --resume typed into the command becomes the session's conversation.
                    var (command, conversation) = TerminalSession.TakeConversation(c.GetString()!.Trim());
                    // Another agent can't open the old one's conversation.
                    if (Agents.For(command) != s.Agent) s.Info.Conversation = null;
                    s.Info.Command = command;
                    if (conversation != null) s.Info.Conversation = conversation;
                    // A command that is now a plain claude (e.g. --continue taken out) gets a conversation of its own
                    // right away, rather than a new one at every start until the next time the app starts: the one it's
                    // in if it's running, otherwise a new one.
                    CatchUpConversation(s);
                    if (TerminalSession.NeedsOwnConversation(s.Info)) s.Info.Conversation = Guid.NewGuid().ToString();
                }
                // The conversation to reopen: one picked from the saved ones, or "new" for a fresh one.
                if (OptStr(msg, "conversation") is { } pick && TerminalSession.CanTrackConversation(s.Info.Command))
                {
                    // Codex picks a new conversation's id itself; the session learns it once Codex has started it.
                    s.Info.Conversation = pick == "new" ? (s.Agent?.ChoosesConversationId == true ? Guid.NewGuid().ToString() : null)
                        : TerminalSession.IsConversationId(pick) ? pick : throw new ArgumentException("That isn't a conversation id.");
                    s.Info.Command = TerminalSession.StripContinue(s.Info.Command);
                }
                if (msg.TryGetProperty("project", out _)) s.Info.Project = ProjectId(msg);
                SaveSessions();
                Post(SessionList());
                return null;
            }

            // ---- projects (a grouping of sessions; deleting one never removes sessions)
            case "projects.add":
            {
                var project = new ProjectInfo { Name = ProjectName(msg), Color = ProjectInfo.NormalizeColor(OptStr(msg, "color")) };
                projects.Add(project);
                ProjectStore.Save(projects);
                Post(SessionList());
                return project.Id;
            }
            case "projects.rename":
            {
                var project = projects.FirstOrDefault(p => p.Id == Str(msg, "id")) ?? throw new KeyNotFoundException("Unknown project");
                project.Name = ProjectName(msg);
                // Sent only by the edit dialog: a missing "color" leaves it alone, null or "" means automatic.
                if (msg.TryGetProperty("color", out _)) project.Color = ProjectInfo.NormalizeColor(OptStr(msg, "color"));
                ProjectStore.Save(projects);
                Post(SessionList());
                return null;
            }
            case "projects.remove":
            {
                var id = Str(msg, "id");
                if (projects.RemoveAll(p => p.Id == id) == 0) return null;
                // Its sessions are kept and simply have no project any more. Sessions first: if saving projects
                // then fails, the sessions are already ungrouped rather than pointing at a missing project.
                foreach (var s in sessions.Values.Where(s => s.Info.Project == id)) s.Info.Project = null;
                SaveSessions();
                ProjectStore.Save(projects);
                Post(SessionList());
                return null;
            }
            case "projects.reorder":
            {
                var ids = msg.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToList();
                if (ids.Count == projects.Count && ids.ToHashSet().SetEquals(projects.Select(p => p.Id)))
                {
                    var byId = projects.ToDictionary(p => p.Id);
                    projects.Clear();
                    projects.AddRange(ids.Select(i => byId[i]));
                    ProjectStore.Save(projects);
                    Post(SessionList());
                }
                return null;
            }
            case "removeSession":
                RemoveSession(Get(msg));
                return null;
            case "panels.scope":
                panelStore.SetScope(Str(msg, "owner"), Str(msg, "id"), Str(msg, "scope"));
                return null;
            case "panels.check":
            {
                // Refused when the panel changed under the tick: the page then gets the panel as it now is.
                var done = panelStore.SetChecked(Str(msg, "owner"), Str(msg, "id"), Str(msg, "path"), Bool(msg, "checked"), Int(msg, "version"));
                if (!done) Post(PanelsMessage());
                return done;
            }
            case "panels.reopen":
                if (!panelStore.Reopen(Str(msg, "owner"), Str(msg, "id"))) throw new InvalidOperationException("That panel can't be reopened any more.");
                return null;
            case "panels.close":
                panelStore.Close(Str(msg, "owner"), Str(msg, "id"));
                return null;

            // ---- messages between sessions (see RelayHub): the rules, and your answers
            case "relay.state":
                return RelayState();
            case "relay.set":
                // to: a session id, "project" or "*"; decision: allow, deny or ask (no rule).
                relay.Policy.Set(Str(msg, "from"), Str(msg, "to"), ParseDecision(Str(msg, "decision")));
                return null;
            case "relay.answer":
            {
                // Your answer to a session asking to message another. scope: "once" (this time only: delivered when
                // allowed, dropped when not), or a rule: "session" (that session), "project" (its project), "*" (anyone),
                // or for every session "all-project" (the same project) or "all" (anyone).
                var from = Str(msg, "from");
                var to = Str(msg, "to");
                var allow = Bool(msg, "allow");
                var (ruleFrom, ruleTo) = Str(msg, "scope") switch
                {
                    "once" => (null, null),
                    RelayPolicy.SameProject => (from, RelayPolicy.SameProject),
                    RelayPolicy.Anyone => (from, RelayPolicy.Anyone),
                    "all-project" => (RelayPolicy.Anyone, RelayPolicy.SameProject),
                    "all" => (RelayPolicy.Anyone, RelayPolicy.Anyone),
                    _ => (from, to),
                };
                if (ruleFrom == null) relay.AnswerOnce(from, to, allow);
                else
                {
                    relay.Policy.Set(ruleFrom, ruleTo!, allow ? RelayDecision.Allow : RelayDecision.Deny);
                    relay.AnswerOnce(from, to, allow);   // what waits, if the rule didn't settle it (a broader rule a closer one overrides)
                }
                return null;
            }
            // ---- sessions starting and driving others (SessionControl)
            case "control.state":
                return ControlState();
            case "control.status":
                Driving.SetStatus(Str(msg, "id"), Str(msg, "status"));
                return null;
            case "control.answer":
                Driving.AnswerSpawn(Str(msg, "id"), Bool(msg, "allow"), OptStr(msg, "scope") ?? "once");
                return null;
            case "control.set":
                Driving.Policy.Set(Str(msg, "ability") switch
                {
                    "spawn" => ControlAbility.Spawn,
                    "answer" => ControlAbility.Answer,
                    "close" => ControlAbility.Close,
                    var a => throw new ArgumentException($"There's no ability \"{a}\"."),
                }, Str(msg, "scope"), ParseDecision(Str(msg, "decision")));
                return null;
            case "control.log":
                return Driving.Log().Select(e => new { at = e.At, from = e.From, target = e.Target, action = e.Action, detail = e.Detail, ok = e.Ok });
            case "control.clearLog":
                Driving.ClearLog();
                return null;
            case "relay.pause":
                relay.Policy.Paused = Bool(msg, "paused");
                return null;
            case "relay.unread":
                return relay.Unread(Str(msg, "id"));
            case "relay.log":
                return relay.Log().Select(e => new
                {
                    id = e.Message.Id, from = e.Message.From, to = e.Message.To, text = e.Message.Text, sent = e.Message.Sent, replyTo = e.Message.ReplyTo,
                    thread = e.Message.Thread, turn = e.Message.Turn, status = e.Status.ToString().ToLowerInvariant(), at = e.At,
                });
            case "relay.clearLog":
                relay.ClearLog();
                return null;

            // ---- plugins' messages, asked by the page for a plugin it checked may (the plugin's id is the frame's own)
            case "relay.pluginTargets":
                return relay.Others(PluginGrants.PeerPrefix + Str(msg, "plugin")).Where(o => o.Decision == RelayDecision.Allow && !o.Peer.Id.StartsWith(PluginGrants.PeerPrefix))
                    .Select(o => new { id = o.Peer.Id, name = o.Peer.Name, project = o.Peer.ProjectName, agent = o.Peer.Agent, running = o.Peer.Running });
            case "relay.pluginSend":
            {
                var r = relay.Send(PluginGrants.PeerPrefix + Str(msg, "plugin"), Str(msg, "to"), Str(msg, "text"), OptStr(msg, "replyTo"));
                return new { outcome = r.Outcome.ToString().ToLowerInvariant(), id = r.Message?.Id };
            }
            case "relay.pluginRead":
                return relay.Read(PluginGrants.PeerPrefix + Str(msg, "plugin")).Select(m => new
                {
                    id = m.Id, from = m.From, fromName = relay.Peer(m.From)?.Name, text = m.Text, sent = m.Sent, replyTo = m.ReplyTo, turn = m.Turn,
                });

            // ---- your answers about plugins (see PluginGrants)
            case "plugins.grants":
                return GrantsMessage();
            case "plugins.grant":
            {
                var id = Str(msg, "id");
                grants.Set(id, Str(msg, "kind"), Str(msg, "state"), plugins.GetValueOrDefault(id)?.ManifestHash);
                return null;
            }
            case "plugins.projects":
                grants.SetProjects(Str(msg, "id"), msg.TryGetProperty("projects", out var pj) && pj.ValueKind == JsonValueKind.Array
                    ? pj.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : null);
                return null;
            case "plugins.hidden":
                grants.SetHidden(Str(msg, "id"), Str(msg, "sessionId"), Bool(msg, "hidden"));
                return null;
            case "plugins.forget":
                grants.Forget(Str(msg, "id"));
                return null;
            case "plugins.grants.import":
            {
                // Answers the page kept itself before they were kept here; taken only into an empty store.
                if (!msg.TryGetProperty("grants", out var g) || g.ValueKind != JsonValueKind.Object) return null;
                List<string> List(JsonElement e, string name) => e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
                grants.Import(g.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).ToDictionary(p => p.Name, p => new PluginGrant(
                    List(p.Value, "allowed"), List(p.Value, "denied"),
                    p.Value.TryGetProperty("projects", out var pr) && pr.ValueKind == JsonValueKind.Array ? List(p.Value, "projects") : null,
                    List(p.Value, "hidden"), null)));
                return null;
            }
            case "reorder":
            {
                var ids = msg.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).Where(id => sessions.ContainsKey(id) || serverSessions.ContainsKey(id)).Distinct().ToList();
                if (ids.Count == order.Count)
                {
                    order.Clear();
                    order.AddRange(ids);
                    SaveSessions();
                }
                return null;
            }
            case "worktree.add":
            {
                // A new worktree of the session's repository, on a new branch; the page then adds a session there.
                var root = GitDir(msg);
                var path = Str(msg, "path").Trim();
                var result = await GitService.AddWorktreeAsync(root, path, Str(msg, "branch").Trim(), OptStr(msg, "base")?.Trim());
                if (!result.Ok) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "git couldn't create the worktree." : result.Error.Trim());
                return Path.GetFullPath(path);
            }
            case "worktrees":
            {
                var dir = Str(msg, "directory");
                return Directory.Exists(dir) ? await GitService.GetWorktreesAsync(dir) : new List<WorktreeInfo>();
            }
            case "worktree.overview":
                // The Project view's rows for the session's repository: every worktree, how far each is from the base
                // branch, and what's changed in it.
                return await GitService.GetWorktreeOverviewAsync(GitDir(msg));
            case "worktree.contents":
            {
                // For Remove worktree's confirmation: what is changed in it, and the ignored files git would delete with it.
                var root = Str(msg, "root").Trim();
                if (!Directory.Exists(root)) throw new InvalidOperationException("The repository folder is gone.");
                return await GitService.GetWorktreeContentsAsync(root, Str(msg, "path").Trim())
                    ?? throw new InvalidOperationException("Couldn't read what is in the worktree.");
            }
            case "worktree.remove":
            {
                // The repository is named rather than a session: the page has already removed the sessions in the
                // worktree (a process with its folder as working directory would keep it), and it may have been the
                // only one. Their processes can take a moment to let go of the folder, hence the retries. Ignored files
                // are deleted only when the page says the person was shown them and agreed (deleteIgnored).
                var root = Str(msg, "root").Trim();
                if (!Directory.Exists(root)) throw new InvalidOperationException("The repository folder is gone.");
                var deleteIgnored = Bool(msg, "deleteIgnored");
                GitResult result = null!;
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    result = await GitService.RemoveWorktreeAsync(root, Str(msg, "path").Trim(), deleteIgnored);
                    if (result.Ok || !result.Error.Contains("failed to delete", StringComparison.OrdinalIgnoreCase) && !result.Error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) break;
                    await Task.Delay(500);
                }
                if (!result.Ok) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "git couldn't remove the worktree." : result.Error.Trim());
                return null;
            }
            case "conversation":
            {
                // The conversation the session's agent is in: a light outline for the rail, or everything for the
                // Conversation tab (full). Nothing is sent again while the page already has this version, and a page
                // that has `count` turns of this conversation only gets its last one (which may have grown) and newer.
                var s = Get(msg);
                var full = Bool(msg, "full");
                if (s.Agent is not { ShowsConversation: true } agent) return new { found = false };
                // Follow the agent to the conversation it's in now, so a new session's first prompt, or the conversation
                // after /new or /clear, shows straight away rather than once the next round of following finds it.
                var pids = s.ProcessIds();
                var others = OthersConversations(s);
                var live = s.IsRunning ? await Task.Run(() => agent.LiveConversation(s, pids, others)) : null;
                if (AdoptConversation(s, live)) { SaveSessions(); Post(SessionList()); }
                // A stopped session shows the conversation it would resume, so a prompt can still be jumped to in it.
                var conv = await Task.Run(() => agent.ReadConversation(s, pids) ?? (s.IsRunning ? null : agent.ReadSavedConversation(s)));
                if (conv == null) return new { found = false };
                var same = OptStr(msg, "sessionId") == conv.SessionId;
                if (same && msg.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt64() == conv.Version)
                    return new { found = true, unchanged = true };
                var from = same ? Math.Clamp(Int(msg, "count") - 1, 0, conv.Turns.Count) : 0;
                return new
                {
                    found = true,
                    sessionId = conv.SessionId,
                    version = conv.Version,
                    from,
                    turns = conv.Turns.Skip(from).Select(t => full
                        ? (object)new { index = t.Index, time = t.Time, prompt = t.Prompt, items = t.Items }
                        : new
                        {
                            index = t.Index,
                            time = t.Time,
                            prompt = Preview(t.Prompt, 300),
                            // Markdown with its line breaks: the page turns it into plain text for the hover card.
                            reply = t.Items.FirstOrDefault(i => i.Kind == "text")?.Text is { } r ? (r.Length <= 1200 ? r : r[..1200]) : null,
                            tools = t.Items.Count(i => i.Kind == "tool"),
                        }),
                };
            }
            case "conversations":
            {
                // Every agent's, latest first; only one agent's when the page asks for it (a session switching conversation).
                var dir = OptStr(msg, "directory");
                var agent = OptStr(msg, "agent");
                var max = string.IsNullOrWhiteSpace(dir) ? 300 : 100;
                return await Task.Run(() => Agents.All.Where(a => agent == null || a.Id == agent)
                    .SelectMany(a => a.ListConversations(dir, max))
                    .OrderByDescending(c => c.Modified).Take(max).ToList());
            }
            case "conversationInfo":
            {
                // The session's own conversation for its tooltips, running or not: saved is false until Claude has saved it.
                var s = Get(msg);
                if (s.Info.Conversation is not { } id) return null;
                var c = await Task.Run(() => s.Agent?.FindConversation(id, s.Info.Directory));
                return new { id, saved = c != null, title = c?.Title, started = c?.Started, modified = c?.Modified };
            }
            case "openFolder":
                OpenFolder(Get(msg).Info.Directory);
                return null;
            case "terminal.paths":
            {
                // Which of the paths in a terminal's output are there (relative ones from the session's folder), to
                // underline as links. Off the UI thread: a mapped drive can be slow to answer.
                var dir = Get(msg).Info.Directory;
                var texts = msg.GetProperty("paths").EnumerateArray().Select(p => p.GetString() ?? "").Take(200).ToArray();
                return await Task.Run(() => texts.Select(t => TerminalPaths.Resolve(t, dir) is { } f ? new { path = f.Path, folder = f.IsFolder } : null).ToArray());
            }
            case "terminal.openPath":
            {
                // A path link: open it (a file never runs: see OpenFileForViewing), or show it in its folder.
                var found = TerminalPaths.Resolve(Str(msg, "path"), Get(msg).Info.Directory)
                    ?? throw new InvalidOperationException("That file or folder isn't there any more.");
                var inFolder = msg.TryGetProperty("inFolder", out var f) && f.ValueKind == JsonValueKind.True;
                if (inFolder) ShowInFolder(found.Path);
                else if (found.IsFolder) OpenFolder(found.Path);
                else await Task.Run(() => OpenFile(found.Path));
                return null;
            }

            // ---- a server's files, for a window on another computer (see ServerFiles)
            case "fs.list":
                return await Task.Run(() => ServerFiles.List(Str(msg, "path")));
            case "fs.open":
            {
                var path = ServerFiles.Resolve(Str(msg, "path"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                if (Directory.Exists(path)) OpenFolder(path);
                else if (File.Exists(path)) await Task.Run(() => OpenFile(path));
                else throw new InvalidOperationException("That file or folder isn't there any more.");
                return null;
            }
            case "upload.begin":
                uploads.Begin(Str(msg, "token"), Str(msg, "name"));
                return null;
            case "upload.data":
                uploads.Write(Str(msg, "token"), Str(msg, "data"));
                return null;
            case "upload.end":
                return uploads.End(Str(msg, "token"));

            // ---- plugins
            case "plugins.list":
            {
                // Read the plugins folder again, so a plugin added or changed shows without restarting.
                var (found, problems) = LoadPlugins();
                grants.Reconcile(found);   // a plugin whose plugin.json changed is asked again
                relay.SetPeers(RelayPeers());
                return new { folder = PluginFolder, plugins = found.Select(p => new { id = p.Id, title = p.Title, description = p.Description, url = window.PluginBase(p.Id) + p.Entry, placement = p.Placement,
                    menu = p.Menu.Select(i => new { label = i.Label, command = i.Command, where = i.Where }), access = p.Access, inbox = p.Inbox, icon = p.Icon,
                    toolbar = p.Toolbar is { } t ? new { icon = t.Icon, tooltip = t.Tooltip, command = t.Command } : null }), problems };
            }
            case "plugins.openFolder":
                Directory.CreateDirectory(PluginFolder);
                OpenFolder(PluginFolder);
                return null;
            // What a plugin allowed to see sessions asks for. Only the app's page asks (as for everything here); it checks
            // the plugin was allowed before it does.
            case "plugins.folders":
                // The top-level folders of the session's repository, with their file counts.
                return await GitService.GetTopFoldersAsync(GitDir(msg));
            case "plugins.activity":
            {
                // What each session has done: the events of its conversation (the one its Claude is in, or the saved one it
                // would resume). The page says which version of each it has, and gets only what's new since; a different
                // conversation starts it over.
                var ids = msg.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
                var have = msg.TryGetProperty("have", out var h) && h.ValueKind == JsonValueKind.Object ? h.Clone() : default;
                var asked = ids.Select(id => (id, s: sessions.GetValueOrDefault(id))).ToList();
                return await Task.Run(() => asked.Select(a => ActivityOf(a.id, a.s, have)).ToList());
            }
            case "plugins.ask":
            {
                // A question about sessions, answered by an agent run headless over their conversations (see SessionAssistant).
                // The page names the sessions, with what their status dots show; a stopped session's saved conversation is read.
                var question = Str(msg, "question");
                var history = msg.TryGetProperty("history", out var hist) && hist.ValueKind == JsonValueKind.Array
                    ? hist.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => new AssistantExchange(Str(x, "question"), Str(x, "answer"))).ToList()
                    : [];
                var about = msg.TryGetProperty("sessions", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && sessions.ContainsKey(Str(x, "id"))).Select(x => x.Clone()).ToList()
                    : [];
                var briefs = await Task.Run(() => about.Select(x => BriefOf(sessions[Str(x, "id")], Str(x, "name"), OptStr(x, "project"), Str(x, "status"))).ToList());
                var a = await SessionAssistant.AskAsync(question, history, briefs, DateTime.Now);
                return new { answer = a.Answer, writer = a.Writer, sessions = a.Sessions };
            }

            // ---- themes
            case "themes.list":
            {
                // Read again each time, so a theme added or changed shows without restarting.
                var (themes, problems) = Themes.ThemeCatalog.Load(BuiltInThemeFolder, ThemeFolder);
                // The names a theme can set, for the page to write every one out when it saves a theme.
                var names = new { colors = Themes.ThemeCatalog.ColorNames, terminal = Themes.ThemeCatalog.TerminalNames, diff = Themes.ThemeCatalog.DiffNames };
                return new { folder = ThemeFolder, themes, problems, names };
            }
            case "themes.save":
                // A copy of the theme on screen, as a new file in the themes folder.
                return Themes.ThemeCatalog.Save(ThemeFolder, BuiltInThemeFolder, msg.GetProperty("theme"));
            case "themes.openFolder":
                Directory.CreateDirectory(ThemeFolder);
                OpenFolder(ThemeFolder);
                return null;

            // ---- git
            case "git.refresh":
            {
                var s = Get(msg);
                await s.InitGitAsync();
                return null;
            }
            case "git.selectRepo":
            {
                var s = Get(msg);
                if (!await s.SelectRepositoryAsync(Str(msg, "root")))
                    throw new InvalidOperationException("That repository isn't in the session's folder any more. Refresh and try again.");
                SaveSessions();
                return null;
            }
            case "git.diff":
                return await GitService.GetDiffAsync(GitDir(msg), Str(msg, "mode"), Str(msg, "path"),
                    OptStr(msg, "origPath"), OptStr(msg, "commit"), OptStr(msg, "parent"));

            case "git.log":
                return await GitService.GetLogAsync(GitDir(msg));

            case "git.commitDetails":
                return await GitService.GetCommitDetailsAsync(GitDir(msg), Str(msg, "sha"));

            case "git.stage":
            case "git.unstage":
            {
                var s = Get(msg);
                var paths = msg.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();
                var result = type == "git.stage"
                    ? await GitService.StageAsync(GitDir(msg), paths)
                    : await GitService.UnstageAsync(GitDir(msg), paths);
                await s.RefreshStatusAsync();
                if (!result.Ok) throw new InvalidOperationException(result.Error);
                return null;
            }
            case "git.undo":
            {
                var s = Get(msg);
                var staged = msg.GetProperty("staged").GetBoolean();
                // The files as the confirmation dialog showed them: whether each is untracked (deleted on undo) or
                // renamed may have changed while it was open, and then the undo is refused.
                var confirmed = msg.GetProperty("files").EnumerateArray()
                    .Select(f => new FileChange(Str(f, "path"), OptStr(f, "origPath"), Str(f, "status"), Bool(f, "untracked"), Bool(f, "inHead")))
                    .ToList();
                var alsoUnstaged = msg.TryGetProperty("alsoUnstaged", out var au) && au.ValueKind == JsonValueKind.Array
                    ? au.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : [];
                try
                {
                    return await GitService.UndoConfirmedAsync(GitDir(msg), staged, confirmed, OptStr(msg, "head"), alsoUnstaged);
                }
                finally
                {
                    await s.RefreshStatusAsync();
                }
            }
            case "git.commit":
            {
                var s = Get(msg);
                var result = await GitService.CommitAsync(GitDir(msg), Str(msg, "message"), msg.GetProperty("all").GetBoolean());
                await s.RefreshStatusAsync();
                if (!result.Ok) throw new InvalidOperationException(string.IsNullOrEmpty(result.Error) ? result.Text : result.Error);
                return result.Text.Trim();
            }
            case "git.branches":
                return (await GitService.GetBranchesAsync(GitDir(msg))).Select(b => new
                {
                    name = b.Name, remote = b.Remote, upstream = b.Upstream, ahead = b.Ahead, behind = b.Behind, gone = b.Gone, worktree = b.Worktree,
                });
            case "git.checkout":
            {
                // The branch picker: switch to a local branch, or to a remote one's local branch (made if needed).
                var s = Get(msg);
                var result = await GitService.CheckoutBranchAsync(GitDir(msg), Str(msg, "branch"), Bool(msg, "remote"), Bool(msg, "stash"));
                await s.RefreshStatusAsync();
                if (!result.Ok) throw new InvalidOperationException(result.Message);
                return result.Message;
            }
            case "git.createBranch":
            {
                // Visual Studio's New Branch: from a branch or commit, switched to unless unticked, with the changes
                // brought along or stashed first.
                var s = Get(msg);
                var result = await GitService.CreateBranchAsync(GitDir(msg), Str(msg, "branch").Trim(), OptStr(msg, "base"),
                    Bool(msg, "checkout"), Bool(msg, "stash"));
                await s.RefreshStatusAsync();
                if (!result.Ok) throw new InvalidOperationException(result.Message);
                return result.Message;
            }
            case "app.version":
                return UpdateService.CurrentVersion.ToString();
            case "notices.check":
                return (await NoticeService.CheckAsync()).Select(n => new { id = n.Id, kind = n.Kind, text = n.Text, linkLabel = n.LinkLabel, linkUrl = n.LinkUrl, color = n.Color });
            case "count.state":
                // Whether you've said yes to being counted (null: not asked yet), for the question along the top and the version menu.
                return CountMessage(TelemetryService.Load());
            case "count.set":
            {
                var state = TelemetryService.SetEnabled(Bool(msg, "enabled"));
                if (state.Enabled == true) _ = TelemetryService.PingIfDueAsync(CountUsage());   // this hour's, now that there's a yes
                return CountMessage(state);
            }
            case "update.check":
            {
                // The page decides when to ask (at start, then every 15 minutes, or from the menu).
                if (Remote != null) return new { current = UpdateService.CurrentVersion.ToString(), available = false };
                update = await UpdateService.CheckAsync();
                return new
                {
                    current = UpdateService.CurrentVersion.ToString(),
                    available = update != null,
                    version = update?.Version.ToString(),
                    name = update?.Name,
                    pageUrl = update?.PageUrl,
                    notes = update?.Notes,
                    canInstall = update?.CanInstall == true,
                    // A Linux package is installed first, after the system asks for an administrator's password.
                    asksPassword = UpdateService.Install is InstallKind.Deb or InstallKind.Rpm,
                };
            }
            case "update.install":
            {
                var info = update ?? throw new InvalidOperationException("Check for updates first.");
                if (!info.CanInstall) throw new InvalidOperationException("This copy of LlamaTree can't install the update itself: get the new version from its release page.");
                var installer = await UpdateService.DownloadAsync(info);
                // The update waits for both the window and this host to end: the installer can't replace files either has open.
                int[] processes = window.ProcessId is { } w ? [Environment.ProcessId, w] : [Environment.ProcessId];
                if (UpdateService.Install == InstallKind.Windows)
                {
                    NoteConversationsForResume();
                    UpdateService.InstallAfterExit(installer, processes);
                }
                else
                {
                    // Installed (or unpacked) now, while a failure can still be shown; the app reopens once it has closed.
                    string finish;
                    try { finish = await LinuxUpdate.PrepareAsync(UpdateService.Install, installer); }
                    finally { try { Directory.Delete(Path.GetDirectoryName(installer)!, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
                    NoteConversationsForResume();
                    LinuxUpdate.FinishAfterExit(finish, processes);
                }
                // Close once this reply is on its way, and end the host: setup waits for both.
                RunOnUi(() => { window.Close(); QuitRequested?.Invoke(); });
                return null;
            }
            case "git.suggestMessage":
                // Visual Studio-style "Generate commit message": the session's agent describes what Commit would take.
                return await CommitMessageService.SuggestAsync(GitDir(msg), OptStr(msg, "detail"), Get(msg).Agent);
            case "git.pr":
            {
                // The open pull request for the session's branch (GitHub or Azure DevOps), plus links for its menu.
                var s = Get(msg);
                var root = GitDir(msg);
                var branch = (await GitService.GetStatusAsync(root)).Branch;
                if (branch == null || branch.StartsWith('(')) return null;
                var host = await PullRequestService.GetHostAsync(root, branch);
                if (host == null) return null;
                PullRequestInfo? pr = null;
                try { pr = await PullRequestService.FindAsync(root, host, branch); }
                catch (Exception) { /* offline, timed out or refused: just no chip */ }
                return new
                {
                    branch,
                    host = host.IsGitHub ? "GitHub" : "Azure DevOps",
                    webUrl = host.WebUrl,
                    pullRequestsUrl = host.PullRequestsUrl,
                    newUrl = host.NewPullRequestUrl(branch),
                    pr = pr == null ? null : new { number = pr.Number, title = pr.Title, url = pr.Url, draft = pr.IsDraft },
                };
            }
            case "git.fetch":
            case "git.pull":
            case "git.push":
            {
                var s = Get(msg);
                var dir = GitDir(msg);
                var result = type switch
                {
                    "git.fetch" => await GitService.FetchAsync(dir),
                    "git.pull" => await GitService.PullAsync(dir),
                    _ => await GitService.PushAsync(dir),
                };
                await s.RefreshStatusAsync();   // ahead/behind and history follow
                // git reports progress and results on stderr; stdout carries the rest (e.g. pull's file summary).
                var text = string.Join('\n', new[] { result.Error, result.Text.Trim() }.Where(t => !string.IsNullOrWhiteSpace(t)));
                if (!result.Ok) throw new InvalidOperationException(text.Length > 0 ? text : $"{type[4..]} failed");
                return text;
            }
            case "openFile":
            {
                var root = GitDir(msg);
                var path = Str(msg, "path");
                var full = GitService.FullPathInRepo(root, path) ?? throw new InvalidOperationException("That file is outside the repository.");
                if (!File.Exists(full)) return null;
                if (GitService.ExistingPathInRepo(root, path) == null)
                    throw new InvalidOperationException("That file is outside the repository (it is reached through a link).");
                await Task.Run(() => OpenFile(full));
                return null;
            }
            case "git.openFolder":
            {
                // A folder of the Git Changes tree. If its changes are deletions it may be gone: the nearest folder
                // above it that's still there, at most the repository's own.
                var root = GitDir(msg);
                var path = Str(msg, "path").TrimEnd('/', '\\');
                if (GitService.FullPathInRepo(root, path) == null) throw new InvalidOperationException("That folder is outside the repository.");
                while (path.Length > 0 && !Directory.Exists(Path.Combine(root, path))) path = Path.GetDirectoryName(path) ?? "";
                var target = path.Length == 0 ? root
                    : GitService.ExistingPathInRepo(root, path) ?? throw new InvalidOperationException("That folder is outside the repository (it is reached through a link).");
                OpenFolder(target);
                return null;
            }
        }
        return null;
    }

    private void NoteConversationsForResume()
    {
        // Sessions with their own conversation keep it in sessions.json; the note is for the others (e.g. a command
        // with its own options the app doesn't look after).
        foreach (var s in sessions.Values) CatchUpConversation(s);
        var map = new Dictionary<string, string>();
        foreach (var s in sessions.Values)
        {
            if (s.IsRunning && LiveConversation(s) is { } live) map[s.Id] = live;
            // A Claude session starts when it's first opened, so one not opened since the last update still has that
            // update's note to use: it's passed on rather than lost.
            else if (!s.IsRunning && s.ResumeOnce != null) map[s.Id] = s.ResumeOnce;
        }
        try { File.WriteAllText(ResumeFile, JsonSerializer.Serialize(map)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* sessions start fresh instead */ }
    }

    private TerminalSession Get(JsonElement msg) =>
        sessions.TryGetValue(Str(msg, "id"), out var s) ? s : throw new KeyNotFoundException("Unknown session");

    /// <summary>
    /// One session's part of a plugins.activity answer: the events of the conversation its Claude is in (or the saved one
    /// it would resume), from the version the page has (<paramref name="have"/>: id → { conversation, version }) on.
    /// </summary>
    private static object ActivityOf(string id, TerminalSession? s, JsonElement have)
    {
        string? conversation = null, path = null;
        if (s != null)
        {
            if (s.IsRunning && ConversationReader.FindLiveSession(s.ProcessIds()) is { } live)
            {
                conversation = live.SessionId;
                path = ConversationReader.FindTranscript(live.SessionId, live.Cwd);
            }
            if (path == null && s.Info.Conversation is { } saved)
            {
                conversation = saved;
                path = ConversationReader.FindTranscript(saved, s.Info.Directory);
            }
        }
        if (path == null) return new { id, found = false, conversation, version = 0L, from = 0, events = Array.Empty<ActivityEvent>() };
        var data = ActivityReader.Read(path);
        var from = 0;
        if (have.ValueKind == JsonValueKind.Object && have.TryGetProperty(id, out var mine) && OptStr(mine, "conversation") == conversation &&
            mine.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number)
            from = (int)Math.Clamp(v.GetInt64(), 0, data.Events.Count);
        return new { id, found = true, conversation, version = data.Version, from, events = data.Events.Skip(from).ToArray() };
    }

    /// <summary>A session as the assistant is told about it: its conversation is the one its agent is in, or the saved one while it's stopped.</summary>
    private static SessionBrief BriefOf(TerminalSession s, string name, string? project, string status)
    {
        var agent = s.Agent;
        var conv = agent == null ? null : (s.IsRunning ? agent.ReadConversation(s, s.ProcessIds()) : null) ?? agent.ReadSavedConversation(s);
        return new SessionBrief(s.Id, name.Length > 0 ? name : s.Id, project, s.Info.Directory, status, agent, conv);
    }

    private string GitDir(JsonElement msg)
    {
        var s = Get(msg);
        return s.RepoRoot ?? s.Info.Directory;
    }

    /// <summary>The count's state for the page: enabled is null until you've answered.</summary>
    private static object CountMessage(CountState s) => new { enabled = s.Enabled, lastSent = s.LastSent?.ToString("yyyy-MM-dd'T'HH:00'Z'") };

    /// <summary>What the count says about use: how many projects and sessions are open. Read on the UI thread.</summary>
    private CountUsage CountUsage() => new(projects.Count, sessions.Count);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? OptStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The start of a text on one line, for the conversation rail's hover cards.</summary>
    private static string? Preview(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= max ? text : text[..max] + "…";
    }
}
