using System.Diagnostics;
using System.Text.Json;
using LlamaTree.Hosting.Link;

namespace LlamaTree.Hosting;

/// <summary>
/// The window's side of the session host (see <see cref="SessionHost"/>): connects to this profile's host, starting
/// one if none is running, passes the app page's messages to it and its messages to the page, and does what only the
/// window can (the folder picker, the clipboard, the title bar's colours, serving plugins' pages, closing).
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IWindow window;
    private readonly List<string> waiting = [];   // the page's messages from before the host answered
    private LinkChannel? channel;
    private bool pageUp;
    private bool closing;
    private bool closeAcknowledged;
    private int reconnects;

    public HostLink(IWindow window)
    {
        this.window = window;
        _ = ConnectAsync(reload: false);
    }

    /// <summary>The window is closing for good: no question, and losing the host is expected.</summary>
    public bool Closing => closing;

    private async Task ConnectAsync(bool reload)
    {
        var stream = await HostEndpoint.ConnectAsync(TimeSpan.FromMilliseconds(300));
        if (stream == null && StartHost())
            for (var i = 0; i < 60 && stream == null; i++) stream = await HostEndpoint.ConnectAsync(TimeSpan.FromMilliseconds(250));
        if (stream == null)
        {
            window.RunOnUi(() => window.PostJson(JsonSerializer.Serialize(new { type = "host.failed" }, JsonOptions)));
            return;
        }
        var link = new LinkChannel(stream);
        link.Received += OnHostMessage;
        link.Closed += () => window.RunOnUi(() => OnLost(link));
        link.Start();
        link.Send(LinkKind.Hello, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            plugins = new { origin = window.Plugins.Origin, key = window.Plugins.Key },
        }, JsonOptions));
        window.RunOnUi(() =>
        {
            channel = link;
            if (reload)
            {
                waiting.Clear();
                pageUp = false;
                window.ReloadPage();
                return;
            }
            foreach (var m in waiting) link.Send(LinkKind.Page, m);
            waiting.Clear();
        });
    }

    /// <summary>Starts this profile's session host (this program, with --host), detached so it outlives the window.</summary>
    private static bool StartHost()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No program path."))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--host");
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A message from the app page (the window has made sure it came from the page).</summary>
    public void Receive(string json)
    {
        pageUp = true;
        if (TryHandleHere(json)) return;
        if (channel is { IsOpen: true } c) c.Send(LinkKind.Page, json);
        else waiting.Add(json);
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

        object? result;
        try
        {
            switch (type)
            {
                case "pickFolder":
                {
                    var initial = Str(msg, "initial");
                    result = window.PickFolder("Choose the folder (worktree) for the session", Directory.Exists(initial) ? initial : null);
                    break;
                }
                case "clipboard.files":
                    result = window.ClipboardFiles();
                    break;
                case "clipboard.text":
                    result = window.ClipboardText();
                    break;
                case "theme":
                    window.ApplyTheme(msg.TryGetProperty("light", out var l) && l.ValueKind == JsonValueKind.True, Str(msg, "background") is { Length: > 0 } b ? b : null);
                    result = null;
                    break;
                case "window.closing":
                    closeAcknowledged = true;   // the page is asking you (see AskToClose)
                    result = null;
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

    private void OnHostMessage(char kind, string json)
    {
        if (kind == LinkKind.Page) window.RunOnUi(() => window.PostJson(json));
        else if (kind == LinkKind.Window) window.RunOnUi(() => DoWindowOp(json));
    }

    private void DoWindowOp(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var op = doc.RootElement;
            switch (Str(op, "op"))
            {
                case "close":
                    closing = true;
                    window.CloseNow();
                    break;
                case "serve":
                    window.ServePlugin(Str(op, "id"), Str(op, "folder"));
                    break;
                case "unserve":
                    window.StopServingPlugin(Str(op, "id"));
                    break;
            }
        }
        catch (JsonException) { }
    }

    /// <summary>The host went away without the window closing (it crashed, or was ended): start a new one and reload the page.</summary>
    private void OnLost(LinkChannel link)
    {
        if (link != channel || closing) return;
        channel = null;
        if (++reconnects > 3) return;   // something keeps ending it: don't loop
        _ = ConnectAsync(reload: true);
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
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
