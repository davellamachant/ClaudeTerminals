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

    /// <summary>Waits between attempts to reach a server again, the last repeated.</summary>
    private static readonly int[] RetrySeconds = [2, 5, 10, 20, 30];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IWindow window;
    private readonly List<string> waiting = [];   // the page's messages from before the host answered
    private LinkChannel? channel;
    private volatile LinkChannel? latest;          // the connection being made or in use
    private Ssh? ssh;   // this connection's ssh, for a server's sessions
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
    public static bool IsValidTarget(string? target) => Ssh.IsValidTarget(target);

    private async Task ConnectAsync(bool reload)
    {
        Stream? stream;
        string? failure = null;
        if (Remote == null)
        {
            stream = await HostEndpoint.ConnectAsync(TimeSpan.FromMilliseconds(300));
            // No safe place for the host's socket: said, not waited out.
            if (stream == null && HostEndpoint.Problem is { } problem) failure = problem;
            else if (stream == null && SessionHost.StartDetached())
                for (var i = 0; i < 60 && stream == null; i++) stream = await HostEndpoint.ConnectAsync(TimeSpan.FromMilliseconds(250));
        }
        else
        {
            ssh = Ssh.Start(Remote);
            (stream, failure) = (ssh.Stream, ssh.Failure);
        }
        if (stream == null)
        {
            window.RunOnUi(() => Failed(failure));
            return;
        }
        var link = new LinkChannel(stream) { Name = Remote ?? "session host" };
        latest = link;
        link.Received += (kind, json) => OnHostMessage(link, kind, json);
        link.Closed += () => window.RunOnUi(() => OnLost(link));
        link.Start();
        link.Send(LinkKind.Hello, JsonSerializer.Serialize(new
        {
            // A window on another computer: the host can't wait for its process (and sends it its plugins' files to serve).
            // Their addresses are made from ids of their own here (see ServedId), never a plugin of this computer's.
            pid = Remote == null ? Environment.ProcessId : (int?)null,
            plugins = new { origin = window.Plugins.Origin, key = window.Plugins.Key, namespaced = Remote != null },
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
                    if (Remote != null) throw new InvalidOperationException("This computer's folder picker can't browse the server's folders.");
                    var initial = Str(msg, "initial");
                    result = window.PickFolder("Choose the folder (worktree) for the session", Directory.Exists(initial) ? initial : null);
                    break;
                }
                case "clipboard.files" when Remote != null:
                {
                    // Files and pictures pasted into a server's session are copied there first: their paths there are pasted.
                    var paths = window.ClipboardFiles();
                    var link = channel ?? throw new InvalidOperationException($"Not connected to {Remote}.");
                    _ = UploadAsync(link, paths).ContinueWith(t => window.RunOnUi(() =>
                    {
                        if (reqId == null) return;
                        if (t.IsCompletedSuccessfully) Reply(reqId.Value, true, t.Result, null);
                        else Reply(reqId.Value, false, null, t.Exception?.InnerException?.Message ?? "The files couldn't be copied to the server.");
                    }), TaskScheduler.Default);
                    return true;
                }
                case "clipboard.files":
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
                case "app.reconnect":
                    if (channel == null && latest is not { IsOpen: true }) _ = ConnectAsync(reload: everWelcomed);
                    break;
                case "window.closing":
                    closeAcknowledged = true;   // the page is asking you (see AskToClose)
                    break;
                // "Let my other computers connect": about the computer this window is on, so only a window of its own
                // sessions asks, and Windows' prompts for it come from the window you're looking at.
                case "remoteAccess.state" when Remote == null && OperatingSystem.IsWindows():
                    result = RemoteAccess.State();
                    break;
                case "remoteAccess.myKey.make" when Remote == null && OperatingSystem.IsWindows():
                    result = RemoteAccess.MakeMyKey();
                    break;
                case "remoteAccess.enable" or "remoteAccess.disable" or "remoteAccess.addKey" or "remoteAccess.removeKey"
                    when Remote == null && OperatingSystem.IsWindows():
                {
                    Task<object?> work = type switch
                    {
                        "remoteAccess.enable" => RemoteAccess.EnableAsync(Bool(msg, "keysOnly"), Bool(msg, "publicNetworks")).ContinueWith(t => { t.GetAwaiter().GetResult(); return (object?)null; }),
                        "remoteAccess.disable" => RemoteAccess.DisableAsync().ContinueWith(t => { t.GetAwaiter().GetResult(); return (object?)null; }),
                        "remoteAccess.addKey" => RemoteAccess.AddKeyAsync(Str(msg, "key")).ContinueWith(t => (object?)t.GetAwaiter().GetResult()),
                        _ => RemoteAccess.RemoveKeyAsync(Str(msg, "fingerprint")).ContinueWith(t => { t.GetAwaiter().GetResult(); return (object?)null; }),
                    };
                    _ = work.ContinueWith(t => window.RunOnUi(() =>
                    {
                        if (reqId == null) return;
                        if (t.IsCompletedSuccessfully) Reply(reqId.Value, true, t.Result, null);
                        else Reply(reqId.Value, false, null, t.Exception?.InnerException?.Message ?? "It didn't finish.");
                    }), TaskScheduler.Default);
                    return true;
                }
                default:
                    if (Remote != null && ServerConnection.OpensFile(type)) Copies.ExpectFile(Str(msg, "path"));   // a file of that name may come for it
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

    // ---------------------------------------------------------------- the window's own requests to the host

    /// <summary>The window's own requests to the host have negative ids, so their replies are told from the page's.</summary>
    private int ownRequests;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> ownReplies = new();
    private const string OwnReplyStart = """{"type":"reply","reqId":-""";

    /// <summary>Sends the host a request of the window's own; the task ends with its result.</summary>
    private Task<JsonElement> RequestAsync(LinkChannel link, object message)
    {
        var id = Interlocked.Decrement(ref ownRequests);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        ownReplies[id] = reply;
        var json = JsonSerializer.SerializeToNode(message, JsonOptions)!.AsObject();
        json["reqId"] = id;
        link.Send(LinkKind.Page, json.ToJsonString());
        return reply.Task.WaitAsync(TimeSpan.FromMinutes(2)).ContinueWith(t =>
        {
            ownReplies.TryRemove(id, out _);
            if (!link.IsOpen && !t.IsCompletedSuccessfully) throw new InvalidOperationException($"The connection to {Remote} ended.");
            return t.GetAwaiter().GetResult();
        }, TaskScheduler.Default);
    }

    /// <summary>A reply to one of the window's own requests (see <see cref="RequestAsync"/>): true if it was one.</summary>
    private bool TakeOwnReply(string json)
    {
        if (!json.StartsWith(OwnReplyStart, StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            if (!ownReplies.TryRemove(m.GetProperty("reqId").GetInt32(), out var reply)) return true;
            if (m.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                reply.TrySetResult(m.TryGetProperty("result", out var r) ? r.Clone() : default);
            else reply.TrySetException(new InvalidOperationException(Str(m, "error")));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
        return true;
    }

    private Task<List<string>> UploadAsync(LinkChannel link, string[] paths) =>
        ServerFiles.UploadAsync(paths, m => RequestAsync(link, m), json => link.Send(LinkKind.Page, json));

    private void OnHostMessage(LinkChannel link, char kind, string json)
    {
        if (kind == LinkKind.Page && TakeOwnReply(json)) return;
        if (kind != LinkKind.Page && kind != LinkKind.Window) return;
        // A server sending faster than the window takes it in is made to wait, rather than the window filling up (see
        // LinkChannel.Hold). This computer's host isn't: it would end the connection instead (see LinkChannel.MaxOutbox).
        var done = Remote != null ? link.Hold(json.Length) : null;
        window.RunOnUi(() =>
        {
            try
            {
                // A server's host says what it likes: its colours are checked before the page puts them in a style.
                if (kind == LinkKind.Page) window.PostJson(Remote != null ? ServerColors.Sanitize(json) : json);
                else DoWindowOp(link, json);
            }
            finally { done?.Invoke(); }
        });
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
                    // The first time: copies of the server's plugins from an earlier run go (it sends them all again),
                    // so the disk only holds what this run counts against their limits (see ServerCopies).
                    if (Remote != null && !everWelcomed) Copies.ClearPluginsExcept(new HashSet<string>());
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
                case "serve":
                {
                    // A server's plugin is served from the copy of its folder it has just sent (see ServerCopies), under
                    // an id of its own (see ServedId).
                    var id = Str(op, "id");
                    if (Remote == null) window.ServePlugin(id, Str(op, "folder"));
                    else if (Plugins.PluginCatalog.IsValidId(id) && Directory.Exists(Copies.PluginFolder(id))) window.ServePlugin(ServedId(id), Copies.PluginFolder(id));
                    break;
                }
                case "unserve":
                    window.StopServingPlugin(ServedId(Str(op, "id")));
                    if (Remote != null) Copies.PluginGone(Str(op, "id"));
                    break;
                // A server's files: one to open here, or a plugin's.
                case "file" when Remote != null:
                    if (Copies.FilePart(Str(op, "token"), Str(op, "name"), Str(op, "data"), Int(op, "index"), Bool(op, "last")) is { } opened)
                    {
                        _ = Task.Run(() => Desktop.OpenFileForViewing(opened, fromServer: true));
                        window.PostJson(JsonSerializer.Serialize(new { type = "files.opened", name = Path.GetFileName(opened), server = hostMachine is { Length: > 0 } m ? m : Remote }, JsonOptions));
                    }
                    break;
                case "plugin.begin" when Remote != null:
                    Copies.PluginBegin(Str(op, "id"));
                    break;
                case "plugin.file" when Remote != null:
                    Copies.PluginPart(Str(op, "id"), Str(op, "path"), Str(op, "data"), Int(op, "index"), Bool(op, "last"));
                    break;
            }
        }
        // Whatever the host sent fails only itself: never the window (this runs on its UI thread) or the connection.
        // A cancellation as the window closes goes on up.
        catch (Exception ex) when (ex is not OperationCanceledException || !closing)
        {
            AppLog.Warn($"{link.Name}: a {AppLog.MessageType(json)} message ({json.Length} characters) failed and was dropped: {AppLog.Describe(ex)}");
        }
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
        foreach (var id in ownReplies.Keys)
            if (ownReplies.TryRemove(id, out var reply)) reply.TrySetException(new InvalidOperationException($"The connection to {Remote ?? "LlamaTree"} ended."));
        copies?.DropAll();
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
            var tried = ssh;
            var said = Task.Run(async () =>
            {
                var errors = tried == null ? "" : await tried.ErrorsAsync(TimeSpan.FromSeconds(2));
                return (errors, windows: tried != null && await tried.FoundWindowsAsync(errors));
            });
            _ = said.ContinueWith(t => window.RunOnUi(() =>
            {
                var (why, windows) = t.Result;
                // It runs Windows, found just now: connected to again at once, with the bridge command for Windows.
                if (windows && !closing && latest == link)
                {
                    _ = ConnectAsync(reload: false);
                    return;
                }
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

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : -1;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// A short name for the server this window shows, for its own single-instance lock and its folder of copies: the
    /// same however the target's letters are cased.
    /// </summary>
    public static string RemoteKey(string remote) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(remote.ToLowerInvariant())))[..16];

    /// <summary>
    /// The id this window serves a plugin under: a server's plugin gets one of its own (see
    /// <see cref="PluginAddressing.RemoteId"/>), so it never shares an address with this computer's plugin of the same id
    /// (the windows share one web view profile). The server's host makes its address from the same id (the hello's
    /// "namespaced").
    /// </summary>
    private static string ServedId(string pluginId) => Remote is { } r ? PluginAddressing.RemoteId(r, pluginId) : pluginId;

    /// <summary>This window's copies of the server's files (see <see cref="ServerCopies"/>).</summary>
    private ServerCopies Copies => copies ??= new ServerCopies(Path.Combine(AppProfile.CustomFolder ?? AppProfile.LocalFolder, "servers", RemoteKey(Remote!)), Remote!);
    private ServerCopies? copies;
}
