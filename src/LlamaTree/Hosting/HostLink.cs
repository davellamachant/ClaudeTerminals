using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LlamaTree.Hosting.Link;
using LlamaTree.Platform;

namespace LlamaTree.Hosting;

/// <summary>
/// The window's side of the session host (see <see cref="SessionHost"/>): connects to this profile's host, starting
/// one if none is running, passes the app page's messages to it and its messages to the page, and does what only the
/// window can (the folder picker, the clipboard, the title bar's colours, opening web pages, serving plugins' pages,
/// closing).
/// <para>
/// A window started with --remote shows the sessions of a host on another computer instead, reached over SSH (see
/// <see cref="HostBridge"/>). Losing the connection then leaves those sessions running there; the window keeps trying
/// to reconnect.
/// </para>
/// <para>
/// Closing the window asks the page first: with sessions running, it asks whether to keep them running without the
/// window or stop them (see the page's closeRequested). A page that doesn't answer leaves them running, as nothing is
/// lost that way.
/// </para>
/// </summary>
internal sealed class HostLink
{
    /// <summary>What the link needs from the window. Called on the window's UI thread, except <see cref="RunOnUi"/>.</summary>
    internal interface IWindow
    {
        /// <summary>Sends a message to the app page.</summary>
        void PostJson(string json);

        /// <summary>Runs <paramref name="action"/> on the UI thread, later; dropped once the window has closed.</summary>
        void RunOnUi(Action action);

        /// <summary>How this window serves plugins (see <see cref="PluginAddressing"/>).</summary>
        PluginAddressing Plugins { get; }

        void ServePlugin(string id, string folder);

        void StopServingPlugin(string id);

        /// <summary>Closes the window without asking.</summary>
        void CloseNow();

        /// <summary>Loads the app page again (after reconnecting to a new host).</summary>
        void ReloadPage();

        string? PickFolder(string title, string? initial);

        string[] ClipboardFiles();

        string? ClipboardText();

        void ApplyTheme(bool light, string? background);
    }

    /// <summary>The SSH target (user@server, or a Host from ~/.ssh/config) whose sessions this window shows, or null for this computer's.</summary>
    public static string? Remote { get; set; }

    /// <summary>The window's title: the app's, and which computer's sessions it shows when they aren't this one's.</summary>
    public static string WindowTitle => Remote is { } r ? $"{AppHost.Title} — {r}" : AppHost.Title;

    /// <summary>What's run on the server: LlamaTree's bridge to its session host. LLAMATREE_REMOTE_COMMAND overrides it.</summary>
    private static string RemoteCommand =>
        Environment.GetEnvironmentVariable("LLAMATREE_REMOTE_COMMAND") is { Length: > 0 } c ? c : "llamatree --attach";

    /// <summary>Waits between attempts to reach a server again, the last repeated.</summary>
    private static readonly int[] RetrySeconds = [2, 5, 10, 20, 30];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IWindow window;
    private readonly List<string> waiting = [];   // the page's messages from before the host answered
    private LinkChannel? channel;
    private volatile LinkChannel? latest;          // the connection being made or in use
    private StringBuilder sshErrors = new();
    private Task sshErrorsDone = Task.CompletedTask;
    private bool pageUp;
    private bool closing;
    private bool closeAcknowledged;
    private int reconnects;
    private bool welcomed;      // this connection's host has said who it is
    private bool everWelcomed;  // some host has: the page has shown sessions
    private bool announced;
    private string? hostVersion, hostMachine;
    private string? failure;   // a host.failed message the page hasn't been sent yet

    public HostLink(IWindow window)
    {
        this.window = window;
        _ = ConnectAsync(reload: false);
    }

    /// <summary>The window is closing for good: no question, and losing the host is expected.</summary>
    public bool Closing => closing;

    /// <summary>Whether <paramref name="target"/> can be handed to ssh as where to connect: never an option, a space or a control character.</summary>
    public static bool IsValidTarget(string? target) =>
        target is { Length: > 0 and <= 255 } && target[0] != '-' && !target.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private async Task ConnectAsync(bool reload)
    {
        Stream? stream;
        string? failure = null;
        if (Remote == null)
        {
            stream = await HostEndpoint.ConnectAsync(TimeSpan.FromMilliseconds(300));
            if (stream == null && SessionHost.StartDetached())
                for (var i = 0; i < 60 && stream == null; i++) stream = await HostEndpoint.ConnectAsync(TimeSpan.FromMilliseconds(250));
        }
        else
        {
            (stream, failure) = StartSsh();
        }
        if (stream == null)
        {
            window.RunOnUi(() => Failed(failure));
            return;
        }
        var link = new LinkChannel(stream);
        latest = link;
        link.Received += (kind, json) => OnHostMessage(link, kind, json);
        link.Closed += () => window.RunOnUi(() => OnLost(link));
        link.Start();
        link.Send(LinkKind.Hello, JsonSerializer.Serialize(new
        {
            // A window on another computer: the host can't wait for its process, or serve it plugins' pages.
            pid = Remote == null ? Environment.ProcessId : (int?)null,
            plugins = Remote == null ? new { origin = window.Plugins.Origin, key = window.Plugins.Key } : null,
            remote = Remote,
        }, JsonOptions));
        window.RunOnUi(() =>
        {
            if (link != latest || !link.IsOpen) return;   // already lost: OnLost has it
            channel = link;
            if (reload)
            {
                waiting.Clear();
                pageUp = false;
                announced = false;
                window.ReloadPage();
                return;
            }
            foreach (var m in waiting) link.Send(LinkKind.Page, m);
            waiting.Clear();
        });
    }

    /// <summary>Runs ssh to the server's bridge; its output and input are the connection. Its errors are kept, to show if it fails.</summary>
    private (Stream?, string?) StartSsh()
    {
        var errors = new StringBuilder();
        var errorsDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sshErrors = errors;
        sshErrorsDone = errorsDone.Task;
        // LLAMATREE_SSH: another ssh program, for one that isn't on the PATH.
        var program = Environment.GetEnvironmentVariable("LLAMATREE_SSH") is { Length: > 0 } p ? p : "ssh";
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // No password prompts (there's no terminal to type one in): a key, or ssh-agent. Keep-alives notice a dead
        // connection within about 45 seconds.
        foreach (var a in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "--", Remote!, RemoteCommand })
            psi.ArgumentList.Add(a);
        Process? ssh;
        try
        {
            ssh = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (null, "ssh isn't installed on this computer (or isn't on the PATH). LlamaTree uses it to reach the server.");
        }
        if (ssh == null) return (null, "ssh didn't start.");
        ssh.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                errorsDone.TrySetResult();   // ssh has ended
                return;
            }
            lock (errors) { if (errors.Length < 4000) errors.AppendLine(e.Data); }
        };
        ssh.BeginErrorReadLine();
        return (new DuplexStream(ssh.StandardOutput.BaseStream, ssh.StandardInput.BaseStream, () =>
        {
            try { if (!ssh.HasExited) ssh.Kill(); } catch (Exception) { }
            ssh.Dispose();
        }), null);
    }

    /// <summary>No host could be reached: the page says why, and can ask to try again. Kept until the page is there (ssh can fail before it has loaded).</summary>
    private void Failed(string? why)
    {
        failure = JsonSerializer.Serialize(new { type = "host.failed", remote = Remote, message = why }, JsonOptions);
        if (pageUp) TellFailure();
    }

    private void TellFailure()
    {
        if (failure == null) return;
        window.PostJson(failure);
        failure = null;
    }

    /// <summary>A message from the app page (the window has made sure it came from the page).</summary>
    public void Receive(string json)
    {
        pageUp = true;
        TellFailure();
        Announce();
        if (TryHandleHere(json)) return;
        if (channel is { IsOpen: true } c) c.Send(LinkKind.Page, json);
        else waiting.Add(json);
    }

    /// <summary>Tells the page whose sessions it shows, once the host has said and the page is there to hear it.</summary>
    private void Announce()
    {
        if (announced || !pageUp || !welcomed) return;
        announced = true;
        window.PostJson(JsonSerializer.Serialize(new
        {
            type = "machine", remote = Remote, machine = hostMachine, version = hostVersion,
            windowVersion = Updates.UpdateService.CurrentVersion.ToString(),
        }, JsonOptions));
    }

    /// <summary>The page's requests only the window can answer.</summary>
    private bool TryHandleHere(string json)
    {
        string type;
        int? reqId;
        JsonElement msg;
        try
        {
            using var doc = JsonDocument.Parse(json);
            msg = doc.RootElement.Clone();
            type = msg.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            reqId = msg.TryGetProperty("reqId", out var r) && r.TryGetInt32(out var id) ? id : null;
        }
        catch (JsonException)
        {
            return true;   // not a message: dropped
        }

        object? result = null;
        try
        {
            switch (type)
            {
                case "pickFolder":
                {
                    if (Remote != null) throw new InvalidOperationException("Type the folder's path on the server: browsing it from here isn't available yet.");
                    var initial = Str(msg, "initial");
                    result = window.PickFolder("Choose the folder (worktree) for the session", Directory.Exists(initial) ? initial : null);
                    break;
                }
                case "clipboard.files":
                    if (Remote != null) throw new InvalidOperationException("Pasting files and pictures into a session on a server isn't available yet. Text pastes as usual.");
                    result = window.ClipboardFiles();
                    break;
                case "clipboard.text":
                    result = window.ClipboardText();
                    break;
                case "theme":
                    window.ApplyTheme(msg.TryGetProperty("light", out var l) && l.ValueKind == JsonValueKind.True, Str(msg, "background") is { Length: > 0 } b ? b : null);
                    break;
                case "openUrl":
                    // A web page opens where the person is: on this computer, whichever computer the sessions are on.
                    if (Uri.TryCreate(Str(msg, "uri"), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                        Desktop.OpenExternal(u.AbsoluteUri);
                    break;
                case "app.openRemote":
                {
                    // Another window, for a server's sessions (this one stays as it is).
                    var target = Str(msg, "target").Trim();
                    if (!IsValidTarget(target)) throw new InvalidOperationException("Enter a server as user@server, or a Host from your SSH config.");
                    var psi = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No program path.")) { UseShellExecute = false };
                    psi.ArgumentList.Add("--remote");
                    psi.ArgumentList.Add(target);
                    Process.Start(psi)?.Dispose();
                    break;
                }
                case "app.reconnect":
                    if (channel == null && latest is not { IsOpen: true }) _ = ConnectAsync(reload: everWelcomed);
                    break;
                case "window.closing":
                    closeAcknowledged = true;   // the page is asking you (see AskToClose)
                    break;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            if (reqId != null) Reply(reqId.Value, false, null, ex.Message);
            return true;
        }
        if (reqId != null) Reply(reqId.Value, true, result, null);
        return true;
    }

    private void Reply(int reqId, bool ok, object? result, string? error) =>
        window.PostJson(JsonSerializer.Serialize(new { type = "reply", reqId, ok, result, error }, JsonOptions));

    private void OnHostMessage(LinkChannel link, char kind, string json)
    {
        if (kind == LinkKind.Page) window.RunOnUi(() => window.PostJson(json));
        else if (kind == LinkKind.Window) window.RunOnUi(() => DoWindowOp(link, json));
    }

    private void DoWindowOp(LinkChannel link, string json)
    {
        if (link != latest) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var op = doc.RootElement;
            switch (Str(op, "op"))
            {
                case "welcome":
                    welcomed = everWelcomed = true;
                    if (Remote != null) reconnects = 0;   // back after a dropped connection: the waits start short again
                    hostVersion = Str(op, "version");
                    hostMachine = Str(op, "machine");
                    Announce();
                    break;
                case "close":
                    closing = true;
                    window.CloseNow();
                    break;
                // A server's plugins are in its folders, which this window can't serve from.
                case "serve" when Remote == null:
                    window.ServePlugin(Str(op, "id"), Str(op, "folder"));
                    break;
                case "unserve" when Remote == null:
                    window.StopServingPlugin(Str(op, "id"));
                    break;
            }
        }
        catch (JsonException) { }
    }

    /// <summary>
    /// The connection ended without the window closing. This computer's host crashed or was ended: start a new one and
    /// reload the page. A server's connection dropped: its sessions run on there, and the window keeps trying to reach
    /// them. A server never reached says why.
    /// </summary>
    private void OnLost(LinkChannel link)
    {
        if (link != latest || closing) return;
        channel = null;
        var reached = welcomed;
        welcomed = false;
        if (Remote == null)
        {
            if (++reconnects > 3) return;   // something keeps ending it: don't loop
            _ = ConnectAsync(reload: true);
            return;
        }
        if (!everWelcomed)
        {
            // Its output can end a moment before ssh has finished saying why: wait for that (briefly).
            var errors = sshErrors;
            _ = Task.WhenAny(sshErrorsDone, Task.Delay(2000)).ContinueWith(_ => window.RunOnUi(() =>
            {
                string why;
                lock (errors) why = errors.ToString().Trim();
                Failed(why.Length > 0 ? why : "The connection ended before LlamaTree answered on the server.");
            }), TaskScheduler.Default);
            return;
        }
        if (reached) window.PostJson(JsonSerializer.Serialize(new { type = "host.reconnecting", remote = Remote }, JsonOptions));
        var wait = RetrySeconds[Math.Min(reconnects++, RetrySeconds.Length - 1)];
        _ = Task.Delay(TimeSpan.FromSeconds(wait)).ContinueWith(_ => window.RunOnUi(() =>
        {
            if (!closing && channel == null && latest == link) _ = ConnectAsync(reload: true);
        }));
    }

    /// <summary>
    /// The user asked to close the window. True to close now; false to leave it open while the page asks whether to keep
    /// the sessions running (its answer closes the window through the host).
    /// </summary>
    public bool AskToClose()
    {
        if (closing || channel is not { IsOpen: true } || !pageUp) return true;   // nothing to ask with: sessions keep running
        closeAcknowledged = false;
        window.PostJson("""{"type":"closeRequested"}""");
        // A page that doesn't take the question (busy, broken) mustn't keep the window open.
        _ = Task.Delay(2000).ContinueWith(_ => window.RunOnUi(() =>
        {
            if (closeAcknowledged || closing) return;
            closing = true;
            window.CloseNow();
        }));
        return false;
    }

    /// <summary>The window has closed: let the host know straight away (it keeps the sessions running, or ends if none are).</summary>
    public void Detach()
    {
        closing = true;
        channel?.Close();
        latest?.Close();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
