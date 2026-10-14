using System.Text;
using System.Text.Json;
using ClaudeTerminals.Git;
using ClaudeTerminals.Sessions;
using ClaudeTerminals.Terminal;
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

    private readonly WebView2 webView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x1f, 0x1f, 0x1f) };
    private readonly Dictionary<string, TerminalSession> sessions = [];
    private readonly List<string> order = [];
    private readonly Dictionary<string, StringBuilder> pendingOutput = [];
    private readonly System.Windows.Forms.Timer flushTimer = new() { Interval = 16 };
    private bool webReady;

    public MainForm()
    {
        Text = "Claude Terminals";
        BackColor = Color.FromArgb(0x1f, 0x1f, 0x1f);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1600, 1000);
        WindowState = FormWindowState.Maximized;
        var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (icon != null) Icon = icon;
        Controls.Add(webView);

        flushTimer.Tick += (_, _) => FlushOutput();
        flushTimer.Start();

        foreach (var info in SessionStore.Load()) AddSessionRuntime(info);
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
            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping("app.local", root, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.Navigate("https://app.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to initialise WebView2:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var running = sessions.Values.Count(s => s.IsRunning);
        if (running > 0 && e.CloseReason == CloseReason.UserClosing &&
            MessageBox.Show(this, $"{running} session(s) are still running and will be terminated.\n\nClose anyway?", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            e.Cancel = true;
            return;
        }
        flushTimer.Stop();
        foreach (var s in sessions.Values) s.Dispose();
        base.OnFormClosing(e);
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
        s.Exited += (session, code) => RunOnUi(() =>
        {
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
        sessions = order.Select(id => sessions[id]).Select(s => new
        {
            id = s.Id,
            name = s.Info.Name,
            directory = s.Info.Directory,
            command = s.Info.Command,
            running = s.IsRunning,
        }),
    };

    private static object StatusMessage(TerminalSession s, GitStatus st) => new
    {
        type = "git.status",
        id = s.Id,
        isRepo = st.IsRepo,
        error = st.Error,
        root = s.RepoRoot,
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
        if (webView.CoreWebView2 == null) return;
        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
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
                Get(msg).Stop();
                return null;

            case "addSessions":
            {
                foreach (var item in msg.GetProperty("sessions").EnumerateArray())
                {
                    var dir = Str(item, "directory");
                    if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
                    var name = Str(item, "name");
                    AddSessionRuntime(new SessionInfo
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(dir.TrimEnd('\\')) : name.Trim(),
                        Directory = dir,
                        Command = string.IsNullOrWhiteSpace(Str(item, "command")) ? "claude" : Str(item, "command").Trim(),
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
                SaveSessions();
                Post(SessionList());
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
                var root = GitDir(msg);
                var staged = msg.GetProperty("staged").GetBoolean();
                var paths = msg.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
                // Re-read status rather than trusting the page: whether a file is untracked (deleted on undo)
                // or renamed must reflect the repository right now, not a view that may be seconds old.
                var status = await GitService.GetStatusAsync(root);
                var items = (staged ? status.Staged : status.Unstaged).Where(f => paths.Contains(f.Path) && f.Status != "U").ToList();
                var result = items.Count > 0 ? await GitService.UndoAsync(root, staged, items) : null;
                await s.RefreshStatusAsync();
                if (result is { Ok: false }) throw new InvalidOperationException(string.IsNullOrEmpty(result.Error) ? "Undo failed" : result.Error);
                return items.Count;
            }
            case "git.commit":
            {
                var s = Get(msg);
                var result = await GitService.CommitAsync(GitDir(msg), Str(msg, "message"), msg.GetProperty("all").GetBoolean());
                await s.RefreshStatusAsync();
                if (!result.Ok) throw new InvalidOperationException(string.IsNullOrEmpty(result.Error) ? result.Text : result.Error);
                return result.Text.Trim();
            }
            case "openFile":
            {
                var full = Path.GetFullPath(Path.Combine(GitDir(msg), Str(msg, "path")));
                if (File.Exists(full)) OpenExternal(full);
                return null;
            }
        }
        return null;
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
