using System.Text;
using System.Text.Json;
using ClaudeTerminals.Git;
using ClaudeTerminals.Panels;
using ClaudeTerminals.Plugins;
using ClaudeTerminals.Sessions;
using ClaudeTerminals.Terminal;
using ClaudeTerminals.Updates;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeTerminals;

/// <summary>
/// Hosts the whole UI in a WebView2 (xterm.js terminals + Monaco diffs) and bridges it to
/// ConPTY sessions and git through JSON web messages.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The only document allowed in the WebView, and the only one whose messages the host acts on.</summary>
    private const string AppPage = "https://app.local/index.html";

    private readonly WebView2 webView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x1f, 0x1f, 0x1f) };
    private readonly Dictionary<string, TerminalSession> sessions = [];
    private readonly List<string> order = [];
    private readonly List<ProjectInfo> projects = ProjectStore.Load();
    private readonly Dictionary<string, StringBuilder> pendingOutput = [];
    private readonly System.Windows.Forms.Timer flushTimer = new() { Interval = 16 };
    private bool webReady;
    private bool closingForUpdate;
    private UpdateInfo? update;

    /// <summary>Each session's conversation, noted just before an update restarts the app (see <see cref="TerminalSession.ResumeOnce"/>).</summary>
    private static string ResumeFile => Path.Combine(SessionStore.AppDataFolder, "resume-after-update.json");

    public MainForm()
    {
        Text = $"Claude Terminals {UpdateService.CurrentVersion}";   // shown in the taskbar and Alt+Tab
        BackColor = Color.FromArgb(0x1f, 0x1f, 0x1f);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1600, 1000);
        WindowState = FormWindowState.Maximized;
        var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (icon != null) Icon = icon;
        Controls.Add(webView);

        flushTimer.Tick += (_, _) => FlushOutput();
        flushTimer.Start();

        var tidied = false;
        foreach (var info in SessionStore.Load())
        {
            // A session whose project no longer exists (e.g. projects.json was lost) just has no project.
            if (info.Project != null && !projects.Any(p => p.Id == info.Project)) info.Project = null;
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

        conversationTimer.Tick += (_, _) => FollowConversations();
        conversationTimer.Start();
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

    private readonly System.Windows.Forms.Timer conversationTimer = new() { Interval = 10_000 };
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
            .Select(s => (Session: s, Pids: s.ProcessIds(), Others: OthersConversations(s))).ToList();
        if (running.Count == 0) return;
        following = true;
        try
        {
            var live = await Task.Run(() => running.Select(r => (r.Session, Id: r.Session.Agent?.LiveConversation(r.Session, r.Pids, r.Others))).ToList());
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
    private PanelServer? panelServer;

    private static string PanelConfigFolder => Path.Combine(SessionStore.AppDataFolder, "panels");

    private static string PluginFolder => Path.Combine(SessionStore.AppDataFolder, "plugins");

    /// <summary>Themes of your own (see <see cref="Themes.ThemeCatalog"/>); the app's own are in wwwroot/themes.</summary>
    private static string ThemeFolder => Path.Combine(SessionStore.AppDataFolder, "themes");
    private static string BuiltInThemeFolder => Path.Combine(AppContext.BaseDirectory, "wwwroot", "themes");
    private readonly Dictionary<string, PluginInfo> plugins = [];

    /// <summary>
    /// Reads the plugins folder and gives each plugin its address: its folder, served as https://&lt;id&gt;.plugin.local/.
    /// Reads from other origins are refused (DenyCors), so neither another plugin nor a page elsewhere can fetch its files.
    /// </summary>
    private (List<PluginInfo> Found, List<string> Problems) LoadPlugins()
    {
        var core = webView.CoreWebView2;
        var (found, problems) = PluginCatalog.Load(PluginFolder);
        foreach (var gone in plugins.Keys.Except(found.Select(p => p.Id)).ToList())
        {
            core.ClearVirtualHostNameToFolderMapping(PluginCatalog.HostFor(gone));
            plugins.Remove(gone);
        }
        foreach (var p in found)
        {
            if (!plugins.TryGetValue(p.Id, out var had) || had.Folder != p.Folder)
                core.SetVirtualHostNameToFolderMapping(p.Host, p.Folder, CoreWebView2HostResourceAccessKind.DenyCors);
            plugins[p.Id] = p;
        }
        return (found, problems);
    }

    private void StartPanels()
    {
        panelStore.KeepOwners(sessions.Keys);
        panelStore.Changed += change => RunOnUi(() => Post(PanelsMessage(change)));
        var mcp = new PanelMcp(panelStore);
        // A first prompt that couldn't go on the command line is typed once Claude is up (it connects as it starts).
        mcp.Initialized += id => RunOnUi(async () =>
        {
            if (!sessions.TryGetValue(id, out var s) || s.PromptToType is not { } prompt) return;
            s.PromptToType = null;
            await Task.Delay(1500);
            Post(new { type = "typePrompt", id, text = prompt });
        });
        try { panelServer = PanelServer.TryStart(mcp); }
        catch (Exception) { /* sessions start without panels */ }
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
        panelStore.KeepOwners(sessions.Keys);
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

    private System.Windows.Forms.Timer? accentTimer;

    private void PostAccent() => Post(new { type = "accent", palette = AccentColors.Read(), lightApps = AccentColors.AppsUseLightTheme() });

    /// <summary>The window's own colours (title bar, and what shows before the page paints) follow the page's theme.</summary>
    /// <param name="background">The page's background ("#rrggbb"), for a theme with its own.</param>
    private void ApplyWindowTheme(bool light, string? background = null)
    {
        var dark = light ? 0 : 1;
        Native.DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        var own = background is { Length: 7 } hex && hex[0] == '#' &&
            int.TryParse(hex[1..], System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? Color.FromArgb(rgb >> 16 & 0xff, rgb >> 8 & 0xff, rgb & 0xff)
            : (Color?)null;
        // Such a theme's title bar takes its colour too (Windows 11; earlier versions keep the dark one).
        var caption = own is { } c ? ColorTranslator.ToWin32(c) : unchecked((int)0xFFFFFFFF) /* DWMWA_COLOR_DEFAULT */;
        Native.DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        var color = own ?? (light ? Color.FromArgb(0xee, 0xee, 0xee) : Color.FromArgb(0x1f, 0x1f, 0x1f));
        webView.DefaultBackgroundColor = color;
        BackColor = color;
    }

    /// <summary>Follows the Windows accent colour when it's changed in Settings.</summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        const int WM_SETTINGCHANGE = 0x001A, WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
        var accentChanged = m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED ||
            (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero &&
             System.Runtime.InteropServices.Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet");
        if (!accentChanged || !webReady) return;
        // Windows sends several of these, and may write the new shades just after: read once things settle.
        if (accentTimer == null)
        {
            accentTimer = new System.Windows.Forms.Timer { Interval = 400 };
            accentTimer.Tick += OnAccentTimer;
        }
        accentTimer.Stop();
        accentTimer.Start();
    }

    private void OnAccentTimer(object? sender, EventArgs e)
    {
        accentTimer?.Stop();
        PostAccent();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var on = 1;
        Native.DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            // A custom profile (CLAUDE_TERMINALS_DATA) keeps its browser data with it, so it never shares a browser process with the default one.
            var userData = Environment.GetEnvironmentVariable("CLAUDE_TERMINALS_DATA") is { Length: > 0 }
                ? Path.Combine(SessionStore.AppDataFolder, "WebView2")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeTerminals", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await webView.EnsureCoreWebView2Async(env);
            var core = webView.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            // A file or link dropped on the window would otherwise replace the app page (and could then talk to the host).
            webView.AllowExternalDrop = false;
            core.NavigationStarting += (_, args) => { if (!IsAppUri(args.Uri)) args.Cancel = true; };
            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping("app.local", root, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            // Plugins are pages in frames of their own (see PluginCatalog): a plugin's frame stays on plugin addresses.
            core.FrameNavigationStarting += (_, args) => { if (!PluginCatalog.IsPluginUri(args.Uri) && args.Uri != "about:blank") args.Cancel = true; };
            // And on its own plugin's address: the page answers a plugin's frame with what that plugin was allowed to see,
            // so another plugin's page mustn't get into it.
            core.FrameCreated += (_, created) =>
            {
                string? host = null;
                created.Frame.NavigationStarting += (_, args) =>
                {
                    if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
                    host ??= u.Host;
                    if (!string.Equals(host, u.Host, StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
                };
            };
            // The terminal's Paste menu item reads the clipboard; without this WebView2 asks "Allow clipboard access?"
            // the first time, a prompt that also takes focus away from the terminal. Only the app's own origin gets it
            // (requests name the origin, not the page; navigation never leaves the app page anyway).
            core.PermissionRequested += (_, args) =>
            {
                if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead &&
                    Uri.TryCreate(args.Uri, UriKind.Absolute, out var origin) &&
                    origin.Scheme == Uri.UriSchemeHttps && string.Equals(origin.Host, "app.local", StringComparison.OrdinalIgnoreCase))
                    args.State = CoreWebView2PermissionState.Allow;
                // A plugin gets nothing: no clipboard, camera, location or notifications, and no prompt asking for them.
                else if (PluginCatalog.IsPluginUri(args.Uri))
                    args.State = CoreWebView2PermissionState.Deny;
            };
            // The page's files come from the install folder and only change when the app is updated, but the browser
            // caches them heuristically from their timestamps; after an upgrade it could keep running the previous
            // version's app.js, possibly mixed with new files. Start each launch with an empty HTTP cache.
            try { await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}"); }
            catch (Exception) { /* best effort: worst case is the old behaviour */ }
            core.Navigate(AppPage);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to initialise WebView2:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var running = sessions.Values.Where(s => s.IsRunning).ToList();
        if (running.Count > 0 && e.CloseReason == CloseReason.UserClosing && !closingForUpdate &&
            MessageBox.Show(this, CloseMessage(running), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            e.Cancel = true;
            return;
        }
        flushTimer.Stop();
        conversationTimer.Stop();
        foreach (var s in sessions.Values) CatchUpConversation(s);   // so each reopens where it was
        foreach (var s in sessions.Values) s.Dispose();
        panelServer?.Dispose();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Closing only stops the sessions: Claude keeps every conversation and the sessions come back with the app (a Claude
    /// session starts when it's first opened), so say that (and which of them carry on their conversation) rather than
    /// that they'll be "terminated".
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
                ? "It comes back the next time you open Claude Terminals and picks up where it left off."
                : "They come back the next time you open Claude Terminals and pick up where they left off.",
            (0, _) => n == 1
                ? "It comes back the next time you open Claude Terminals, with a new conversation (the current one can be reopened with \"Resume a different conversation…\")."
                : "They come back the next time you open Claude Terminals, with new conversations (the current ones can be reopened with \"Resume a different conversation…\").",
            _ => $"They come back the next time you open Claude Terminals: {carryOn} pick{(carryOn == 1 ? "s" : "")} up where {(carryOn == 1 ? "it" : "they")} left off, " +
                 $"{fresh} start{(fresh == 1 ? "s" : "")} a new conversation (the current one can be reopened with \"Resume a different conversation…\").",
        };
        return $"{sessionsAre} running. Closing stops {(n == 1 ? "it" : "them")}, but nothing is lost: Claude keeps every conversation.\n\n" +
               $"{restart}\n\nAnything Claude is in the middle of will be interrupted.\n\nClose Claude Terminals?";
    }

    // ---------------------------------------------------------------- sessions

    private TerminalSession AddSessionRuntime(SessionInfo info)
    {
        var s = new TerminalSession(info);
        s.Output += (session, text) =>
        {
            lock (pendingOutput)
            {
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
        });
        s.StatusChanged += (session, status) => RunOnUi(() => Post(StatusMessage(session, status)));
        sessions[info.Id] = s;
        order.Add(info.Id);
        _ = s.InitGitAsync();
        return s;
    }

    private void SaveSessions() => SessionStore.Save(order.Select(id => sessions[id].Info));

    private object SessionList() => new
    {
        type = "sessions",
        agents = Agents.All.Select(a => a.Describe()),
        projects = projects.Select(p => new { id = p.Id, name = p.Name, color = p.Color }),
        sessions = order.Select(id => sessions[id]).Select(s => new
        {
            id = s.Id,
            name = s.Info.Name,
            directory = s.Info.Directory,
            command = s.Info.Command,
            project = s.Info.Project,
            conversation = s.Info.Conversation,
            running = s.IsRunning,
        }),
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

    private void Post(object message)
    {
        if (webView.CoreWebView2 is not { } core || !IsAppUri(core.Source)) return;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    /// <summary>True for the app page itself (ignoring any query or fragment); false for every other document, including others under app.local.</summary>
    internal static bool IsAppUri(string? uri) =>
        uri != null && string.Equals(uri.Split('#', '?')[0], AppPage, StringComparison.OrdinalIgnoreCase);

    private void RunOnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsAppUri(e.Source)) return;
        JsonElement msg;
        try
        {
            msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
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
        switch (type)
        {
            case "ready":
                webReady = true;
                PostAccent();   // first: with Windows' app mode the page knows its theme before sessions start
                Post(SessionList());
                Post(PanelsMessage());
                if (newConversations.Count > 0)
                {
                    Post(new { type = "newConversations", ids = newConversations });
                    newConversations.Clear();
                }
                foreach (var s in sessions.Values) _ = s.RefreshStatusAsync();
                return null;

            case "start":
            {
                var s = Get(msg);
                s.Start(Int(msg, "cols"), Int(msg, "rows"), OptStr(msg, "theme"), PanelLinkFor(s));
                return null;
            }
            case "input":
                Get(msg).Write(Str(msg, "data"));
                return null;

            case "resize":
                Get(msg).Resize(Int(msg, "cols"), Int(msg, "rows"));
                return null;

            case "restart":
            {
                var s = Get(msg);
                CatchUpConversation(s);   // a session with its own conversation restarts in the one it's in now
                // Restarting to pick up a new theme: carry on in the exact conversation the session is in.
                if (Bool(msg, "resume") && s.IsRunning && LiveConversation(s) is { } live)
                    s.ResumeOnce = live;
                s.Stop();
                s.Start(Int(msg, "cols"), Int(msg, "rows"), OptStr(msg, "theme"), PanelLinkFor(s));
                return null;
            }
            case "theme":
                ApplyWindowTheme(Bool(msg, "light"), OptStr(msg, "background"));
                return null;
            case "stop":
            {
                // Stopping disposes the process without raising Exited, so tell the page itself (code null = stopped).
                var s = Get(msg);
                CatchUpConversation(s);
                if (s.Stop())
                {
                    FlushOutput();
                    Post(new { type = "exited", id = s.Id, code = (int?)null });
                }
                return null;
            }

            case "addSessions":
            {
                var items = msg.GetProperty("sessions").EnumerateArray()
                    .Select(item => (Dir: Str(item, "directory"), Name: Str(item, "name"), Command: Str(item, "command"), Project: ProjectId(item),
                        Prompt: OptStr(item, "prompt")?.Trim() is { Length: > 0 and <= PanelSpec.MaxPrompt } p ? p : null,
                        Conversation: OptStr(item, "conversation") is { } conv && TerminalSession.IsConversationId(conv) ? conv : null))
                    .ToList();
                // All or nothing: check every folder before adding any. Adding as we went left the earlier sessions
                // added but unsaved and unshown after a failure, and a retry then added them a second time.
                foreach (var item in items)
                    if (!Directory.Exists(item.Dir)) throw new DirectoryNotFoundException(item.Dir);
                foreach (var item in items)
                {
                    var info = new SessionInfo
                    {
                        Name = string.IsNullOrWhiteSpace(item.Name) ? Path.GetFileName(item.Dir.TrimEnd('\\')) : item.Name.Trim(),
                        Directory = item.Dir,
                        Command = string.IsNullOrWhiteSpace(item.Command) ? "claude" : item.Command.Trim(),
                        Project = item.Project,
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
                }
                SaveSessions();
                Post(SessionList());
                return null;
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
            {
                var s = Get(msg);
                s.Dispose();
                sessions.Remove(s.Id);
                order.Remove(s.Id);
                SaveSessions();
                ForgetPanels(s.Id);   // its panels go with it
                Post(SessionList());
                return null;
            }
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
            case "reorder":
            {
                var ids = msg.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).Where(sessions.ContainsKey).ToList();
                if (ids.Count == order.Count)
                {
                    order.Clear();
                    order.AddRange(ids);
                    SaveSessions();
                }
                return null;
            }
            case "pickFolder":
            {
                using var dlg = new FolderBrowserDialog { Description = "Choose the folder (worktree) for the session", UseDescriptionForTitle = true };
                var initial = Str(msg, "initial");
                if (Directory.Exists(initial)) dlg.InitialDirectory = initial;
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
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
            case "worktree.remove":
            {
                // The repository is named rather than a session: the page has already removed the sessions in the
                // worktree (a process with its folder as working directory would keep it), and it may have been the
                // only one. Their processes can take a moment to let go of the folder, hence the retries.
                var root = Str(msg, "root").Trim();
                if (!Directory.Exists(root)) throw new InvalidOperationException("The repository folder is gone.");
                GitResult result = null!;
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    result = await GitService.RemoveWorktreeAsync(root, Str(msg, "path").Trim());
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
                var conv = await Task.Run(() => agent.ReadConversation(s, pids));
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
            case "openUrl":
            {
                if (Uri.TryCreate(Str(msg, "uri"), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                    OpenExternal(u.AbsoluteUri);
                return null;
            }
            case "openFolder":
                OpenExternal(Get(msg).Info.Directory);
                return null;

            // ---- plugins
            case "plugins.list":
            {
                // Read the plugins folder again, so a plugin added or changed shows without restarting.
                var (found, problems) = LoadPlugins();
                return new { folder = PluginFolder, plugins = found.Select(p => new { id = p.Id, title = p.Title, description = p.Description, url = p.Url, placement = p.Placement,
                    menu = p.Menu.Select(i => new { label = i.Label, command = i.Command, where = i.Where }), access = p.Access,
                    toolbar = p.Toolbar is { } t ? new { icon = t.Icon, tooltip = t.Tooltip, command = t.Command } : null }), problems };
            }
            case "plugins.openFolder":
                Directory.CreateDirectory(PluginFolder);
                OpenExternal(PluginFolder);
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
                OpenExternal(ThemeFolder);
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
            case "app.version":
                return UpdateService.CurrentVersion.ToString();
            case "update.check":
            {
                // The page decides when to ask (at start, then every few hours, or from the menu).
                update = await UpdateService.CheckAsync();
                return new
                {
                    current = UpdateService.CurrentVersion.ToString(),
                    available = update != null,
                    version = update?.Version.ToString(),
                    name = update?.Name,
                    pageUrl = update?.PageUrl,
                    notes = update?.Notes,
                    canInstall = update?.Sha256 != null,
                };
            }
            case "update.install":
            {
                var info = update ?? throw new InvalidOperationException("Check for updates first.");
                var installer = await UpdateService.DownloadAsync(info);
                NoteConversationsForResume();
                UpdateService.InstallAfterExit(installer);
                // Close once this reply is on its way; setup waits for the process to end.
                BeginInvoke(() => { closingForUpdate = true; Close(); });
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
                OpenFileForViewing(full);
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

    // Script and interpreter types that Windows' own unsafe-type list (AssocIsDangerous) may not cover.
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".py", ".pyw", ".pyz", ".sh", ".bash", ".rb", ".pl", ".jar",
        ".appinstaller", ".appx", ".appxbundle", ".msix", ".msixbundle", ".application", ".appref-ms",
    };

    // Programs that run the file they are handed rather than display it.
    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "py", "pyw", "node", "deno", "bun", "bash", "sh", "git-bash",
        "wsl", "java", "javaw", "perl", "ruby", "rubyw", "php", "lua", "tclsh", "wish", "rundll32", "regsvr32", "msiexec",
        "regedit", "dotnet", "autohotkey", "autohotkey32", "autohotkey64", "autohotkeyu32", "autohotkeyu64",
    };

    /// <summary>True if the shell's default action for this file type runs it rather than showing it.</summary>
    internal static bool IsRunnable(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length <= 1) return false;
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC";
        return ScriptExtensions.Contains(ext)
            || pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(ext, StringComparer.OrdinalIgnoreCase)
            || Native.AssocIsDangerous(ext)
            || DefaultActionRuns(Native.AssocQuery(Native.ASSOCSTR_COMMAND, ext), Native.AssocQuery(Native.ASSOCSTR_EXECUTABLE, ext));
    }

    /// <summary>
    /// Catches script types this machine has associated with an interpreter (or with the file itself, as for .exe)
    /// even when the extension is on none of the lists above.
    /// </summary>
    /// <param name="command">The default verb's command line, e.g. <c>"%1" %*</c>.</param>
    /// <param name="program">The program that command runs.</param>
    internal static bool DefaultActionRuns(string? command, string? program)
    {
        var cmd = command?.TrimStart() ?? "";
        if (cmd.StartsWith("\"%1\"", StringComparison.Ordinal) || cmd.StartsWith("%1", StringComparison.Ordinal)) return true;
        if (string.IsNullOrEmpty(program)) return false;
        var name = Path.GetFileNameWithoutExtension(program);
        return ScriptHosts.Contains(name) || name.StartsWith("python", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens a changed file the way Explorer would, except that anything Windows would run (scripts, executables)
    /// is opened for editing instead: "Open" must never execute a file Claude just wrote.
    /// </summary>
    private static void OpenFileForViewing(string path)
    {
        if (!IsRunnable(path))
        {
            OpenExternal(path);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No "edit" verb for this type (e.g. .exe, .py).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { ArgumentList = { path } });
        }
    }

    private static void OpenExternal(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing useful to do if the shell can't open it.
        }
    }
}
