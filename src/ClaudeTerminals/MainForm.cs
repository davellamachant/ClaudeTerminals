using System.Text;
using System.Text.Json;
using ClaudeTerminals.Git;
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

        foreach (var info in SessionStore.Load())
        {
            // A session whose project no longer exists (e.g. projects.json was lost) just has no project.
            if (info.Project != null && !projects.Any(p => p.Id == info.Project)) info.Project = null;
            AddSessionRuntime(info);
        }
        ApplyResumeAfterUpdate();
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

    private void PostAccent() => Post(new { type = "accent", palette = AccentColors.Read() });

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
            // The terminal's Paste menu item reads the clipboard; without this WebView2 asks "Allow clipboard access?"
            // the first time, a prompt that also takes focus away from the terminal. Only the app's own origin gets it
            // (requests name the origin, not the page; navigation never leaves the app page anyway).
            core.PermissionRequested += (_, args) =>
            {
                if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead &&
                    Uri.TryCreate(args.Uri, UriKind.Absolute, out var origin) &&
                    origin.Scheme == Uri.UriSchemeHttps && string.Equals(origin.Host, "app.local", StringComparison.OrdinalIgnoreCase))
                    args.State = CoreWebView2PermissionState.Allow;
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
        foreach (var s in sessions.Values) s.Dispose();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Closing only stops the sessions: Claude keeps every conversation and the sessions start again with the app, so say
    /// that (and which of them carry on their conversation) rather than that they'll be "terminated".
    /// </summary>
    internal static string CloseMessage(IReadOnlyCollection<TerminalSession> running)
    {
        var n = running.Count;
        var carryOn = running.Count(s => TerminalSession.CarriesOnConversation(s.Info.Command));
        var fresh = n - carryOn;
        var sessionsAre = n == 1 ? "1 session is" : $"{n} sessions are";
        var restart = (carryOn, fresh) switch
        {
            (_, 0) => n == 1
                ? "It starts again the next time you open Claude Terminals and picks up where it left off."
                : "They start again the next time you open Claude Terminals and pick up where they left off.",
            (0, _) => n == 1
                ? "It starts again the next time you open Claude Terminals, with a new conversation (the current one can be reopened with \"Resume a different conversation…\")."
                : "They start again the next time you open Claude Terminals, with new conversations (the current ones can be reopened with \"Resume a different conversation…\").",
            _ => $"They start again the next time you open Claude Terminals: {carryOn} pick{(carryOn == 1 ? "s" : "")} up where {(carryOn == 1 ? "it" : "they")} left off, " +
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
        projects = projects.Select(p => new { id = p.Id, name = p.Name, color = p.Color }),
        sessions = order.Select(id => sessions[id]).Select(s => new
        {
            id = s.Id,
            name = s.Info.Name,
            directory = s.Info.Directory,
            command = s.Info.Command,
            project = s.Info.Project,
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
                Post(SessionList());
                PostAccent();
                foreach (var s in sessions.Values) _ = s.RefreshStatusAsync();
                return null;

            case "start":
            {
                var s = Get(msg);
                s.Start(Int(msg, "cols"), Int(msg, "rows"));
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
                s.Stop();
                s.Start(Int(msg, "cols"), Int(msg, "rows"));
                return null;
            }
            case "stop":
            {
                // Stopping disposes the process without raising Exited, so tell the page itself (code null = stopped).
                var s = Get(msg);
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
                    .Select(item => (Dir: Str(item, "directory"), Name: Str(item, "name"), Command: Str(item, "command"), Project: ProjectId(item)))
                    .ToList();
                // All or nothing: check every folder before adding any. Adding as we went left the earlier sessions
                // added but unsaved and unshown after a failure, and a retry then added them a second time.
                foreach (var item in items)
                    if (!Directory.Exists(item.Dir)) throw new DirectoryNotFoundException(item.Dir);
                foreach (var item in items)
                {
                    AddSessionRuntime(new SessionInfo
                    {
                        Name = string.IsNullOrWhiteSpace(item.Name) ? Path.GetFileName(item.Dir.TrimEnd('\\')) : item.Name.Trim(),
                        Directory = item.Dir,
                        Command = string.IsNullOrWhiteSpace(item.Command) ? "claude --continue" : item.Command.Trim(),
                        Project = item.Project,
                    });
                }
                SaveSessions();
                Post(SessionList());
                return null;
            }
            case "updateSession":
            {
                var s = Get(msg);
                if (msg.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString())) s.Info.Name = n.GetString()!.Trim();
                if (msg.TryGetProperty("command", out var c) && !string.IsNullOrWhiteSpace(c.GetString())) s.Info.Command = c.GetString()!.Trim();
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
                Post(SessionList());
                return null;
            }
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
            case "worktrees":
            {
                var dir = Str(msg, "directory");
                return Directory.Exists(dir) ? await GitService.GetWorktreesAsync(dir) : new List<WorktreeInfo>();
            }
            case "conversation":
            {
                // The conversation the session's Claude is in: a light outline for the rail, or everything for the
                // Conversation tab (full). Nothing is sent again while the page already has this version, and a page
                // that has `count` turns of this conversation only gets its last one (which may have grown) and newer.
                var s = Get(msg);
                var full = Bool(msg, "full");
                var conv = await Task.Run(() => ConversationReader.ForProcesses(s.ProcessIds()));
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
                            reply = Preview(t.Items.FirstOrDefault(i => i.Kind == "text")?.Text, 300),
                            tools = t.Items.Count(i => i.Kind == "tool"),
                        }),
                };
            }
            case "conversations":
            {
                var dir = OptStr(msg, "directory");
                return await Task.Run(() => ConversationIndex.List(dir, string.IsNullOrWhiteSpace(dir) ? 300 : 100));
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

            // ---- git
            case "git.refresh":
            {
                var s = Get(msg);
                await s.InitGitAsync();
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
                // Visual Studio-style "Generate commit message": Claude describes what Commit would take.
                return await CommitMessageService.SuggestAsync(GitDir(msg));
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
        var map = new Dictionary<string, string>();
        foreach (var s in sessions.Values.Where(s => s.IsRunning))
            if (ConversationReader.FindLiveSession(s.ProcessIds()) is { } live) map[s.Id] = live.SessionId;
        try { File.WriteAllText(ResumeFile, JsonSerializer.Serialize(map)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* sessions start fresh instead */ }
    }

    private TerminalSession Get(JsonElement msg) =>
        sessions.TryGetValue(Str(msg, "id"), out var s) ? s : throw new KeyNotFoundException("Unknown session");

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
