using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaTree.Hosting.Link;
using LlamaTree.Platform;

namespace LlamaTree.Hosting;

/// <summary>
/// This computer's session host's connection to a server's, for the sessions it shows from there among its own (see
/// AppHost.Servers). To the server's host it's a window on another computer: it hears what that host tells its page
/// (passed on, see <see cref="PageMessage"/>), sends it requests, and opens the files it's sent here. A dropped
/// connection is made again, waiting longer each time; the server's sessions run on meanwhile.
/// Everything is raised on the app thread (<see cref="runOnUi"/>).
/// </summary>
internal sealed class ServerConnection : IDisposable
{
    private static readonly int[] RetrySeconds = [2, 5, 10, 20, 30, 60];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Action<Action> runOnUi;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> replies = new();
    private readonly ServerCopies copies;
    private LinkChannel? link;   // the connection in use, once the server's host has answered
    private int requests;
    private int failures;
    private bool disposed;
    private int attempt;          // which connection attempt is current: a later one makes an earlier one's ending moot

    public ServerConnection(string target, Action<Action> runOnUi)
    {
        Target = target;
        this.runOnUi = runOnUi;
        copies = new ServerCopies(Path.Combine(AppProfile.CustomFolder ?? AppProfile.LocalFolder, "servers", HostLink.RemoteKey(target)));
    }

    /// <summary>How the server is reached: user@server, or a Host from the SSH config.</summary>
    public string Target { get; }

    /// <summary>"connecting", "online", "offline" (see <see cref="Problem"/>), or "elsewhere": shown in another window, which took over.</summary>
    public string State { get; private set; } = "connecting";

    /// <summary>Why it's offline: what ssh or the server said.</summary>
    public string? Problem { get; private set; }

    /// <summary>The server's name, and its LlamaTree's version, once it has said.</summary>
    public string? Machine { get; private set; }
    public string? Version { get; private set; }

    public bool IsOnline => State == "online" && link is { IsOpen: true };

    /// <summary>The server's host told its page something (a session's output, its session list…): the JSON, on the app thread.</summary>
    public event Action<ServerConnection, string>? PageMessage;

    /// <summary>A server session's call to a tool (its id there, the JSON-RPC message), to answer with <see cref="Send"/>: { type: "tools.reply", id, reply }.</summary>
    public event Action<ServerConnection, int, string, JsonElement>? ToolCall;

    /// <summary>A copy of one of the server's files was opened here (its name): the page says it's a read-only copy.</summary>
    public event Action<ServerConnection, string>? FileOpened;

    /// <summary>The server's plugins have changed (one added, removed or sent again): see <see cref="ServerPlugins"/>.</summary>
    public event Action<ServerConnection>? PluginsChanged;

    private readonly HashSet<string> served = [];

    /// <summary>
    /// The server's plugins, from the copies of their folders it has sent here (their ids there): what's in its plugins
    /// folder, read as this computer's would be.
    /// </summary>
    public (List<LlamaTree.Plugins.PluginInfo> Plugins, List<string> Problems) ServerPlugins()
    {
        var (found, problems) = LlamaTree.Plugins.PluginCatalog.Load(Path.GetDirectoryName(copies.PluginFolder("x"))!);
        return (found.Where(p => served.Contains(p.Id)).ToList(), problems);
    }

    /// <summary><see cref="State"/> changed, or the host came back (the page should be told again what its sessions show).</summary>
    public event Action<ServerConnection>? StateChanged;

    /// <summary>Connects, unless connected or connecting already.</summary>
    public void Connect()
    {
        if (disposed || link != null || (State == "connecting" && attempt > 0)) return;
        _ = ConnectAsync(++attempt);
    }

    /// <summary>Connects again now (after a failure, or after another window took over), without waiting for the next try.</summary>
    public void Reconnect()
    {
        if (disposed || link != null) return;
        failures = 0;
        SetState("connecting", null);
        _ = ConnectAsync(++attempt);
    }

    private async Task ConnectAsync(int mine)
    {
        SetState("connecting", Problem);
        var ssh = await Task.Run(() => Ssh.Start(Target));
        if (ssh.Stream == null)
        {
            runOnUi(() => Lost(mine, ssh.Failure, reached: false));
            return;
        }
        var channel = new LinkChannel(ssh.Stream);
        var welcomed = false;
        channel.Received += (kind, json) =>
        {
            if (kind == LinkKind.Page)
            {
                if (TakeReply(json)) return;
                runOnUi(() => { if (link == channel) PageMessage?.Invoke(this, json); });
            }
            else if (kind == LinkKind.Window)
            {
                runOnUi(() =>
                {
                    if (mine != attempt) return;
                    if (WindowOp(channel, json) == "welcome") welcomed = true;
                });
            }
        };
        channel.Closed += () =>
        {
            // ssh's last words say why, when the server never answered.
            _ = (welcomed ? Task.FromResult("") : ssh.ErrorsAsync(TimeSpan.FromSeconds(2))).ContinueWith(t =>
                runOnUi(() => Lost(mine, t.Result, welcomed)), TaskScheduler.Default);
        };
        channel.Start();
        // To the server's host this is a window on another computer: it opens nothing on its own desktop, sending files
        // here instead, and sends no plugins (this host doesn't show a server's plugins). Its sessions' tool calls
        // (panels, messages) are answered here, so they show and reach sessions here too (see ToolCall).
        // Its plugins' folders are sent here, and shown among this computer's plugins (see ServerPlugins).
        channel.Send(LinkKind.Hello, JsonSerializer.Serialize(new { pid = (int?)null, plugins = new { origin = (string?)null, key = (string?)null }, remote = Environment.MachineName, tools = true }, JsonOptions));
        runOnUi(() => { if (mine == attempt && !disposed) pending = channel; else channel.Close(); });
    }

    private LinkChannel? pending;   // connected, waiting for the server's host to say who it is

    /// <summary>Something only a window does, asked by the server's host. Returns the op.</summary>
    private string WindowOp(LinkChannel channel, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var op = doc.RootElement;
            var name = Str(op, "op");
            switch (name)
            {
                case "welcome":
                    if (pending != channel) break;
                    link = channel;
                    pending = null;
                    failures = 0;
                    Machine = Str(op, "machine");
                    Version = Str(op, "version");
                    SetState("online", null);
                    // As a page does when it loads: the host sends its sessions, what they've shown, and their output from now on.
                    channel.Send(LinkKind.Page, """{"type":"ready"}""");
                    // Its plugins folder, read: each plugin's folder is sent here, then served (see ServerPlugins).
                    channel.Send(LinkKind.Page, """{"type":"plugins.list"}""");
                    break;
                case "close":
                    // Another window took over the server's sessions (only one shows them at a time). Not taken back
                    // until asked (Reconnect), or the two would take turns.
                    State = "elsewhere";
                    Problem = "Another LlamaTree window is showing this server's sessions.";
                    channel.Close();
                    break;
                case "tools":
                    if (link == channel && op.TryGetProperty("id", out var callId) && callId.TryGetInt32(out var call) && op.TryGetProperty("message", out var message))
                        ToolCall?.Invoke(this, call, Str(op, "session"), message.Clone());
                    break;
                case "plugin.begin":
                    copies.PluginBegin(Str(op, "id"));
                    break;
                case "plugin.file":
                    copies.PluginPart(Str(op, "id"), Str(op, "path"), Str(op, "data"), Int(op, "index"), op.TryGetProperty("last", out var end) && end.ValueKind == JsonValueKind.True);
                    break;
                case "serve":
                    // Its copy has all come.
                    if (LlamaTree.Plugins.PluginCatalog.IsValidId(Str(op, "id")) && served.Add(Str(op, "id"))) PluginsChanged?.Invoke(this);
                    else if (served.Contains(Str(op, "id"))) PluginsChanged?.Invoke(this);
                    break;
                case "unserve":
                    if (served.Remove(Str(op, "id"))) PluginsChanged?.Invoke(this);
                    break;
                case "file":
                    if (copies.FilePart(Str(op, "token"), Str(op, "name"), Str(op, "data"), Int(op, "index"), op.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.True) is { } opened)
                    {
                        _ = Task.Run(() => Desktop.OpenFileForViewing(opened));
                        FileOpened?.Invoke(this, Path.GetFileName(opened));
                    }
                    break;
            }
            return name;
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private void Lost(int mine, string? why, bool reached)
    {
        if (mine != attempt || disposed) return;
        link = null;
        pending = null;
        copies.DropAll();
        foreach (var id in replies.Keys)
            if (replies.TryRemove(id, out var r)) r.TrySetException(new InvalidOperationException($"The connection to {Target} ended."));
        if (State == "elsewhere")
        {
            StateChanged?.Invoke(this);
            return;
        }
        SetState("offline", reached ? "The connection dropped." : string.IsNullOrWhiteSpace(why) ? "The connection ended before LlamaTree answered on the server." : why);
        var wait = RetrySeconds[Math.Min(failures++, RetrySeconds.Length - 1)];
        _ = Task.Delay(TimeSpan.FromSeconds(wait)).ContinueWith(_ => runOnUi(() =>
        {
            if (mine == attempt && !disposed && link == null && State == "offline") _ = ConnectAsync(++attempt);
        }), TaskScheduler.Default);
    }

    private void SetState(string state, string? problem)
    {
        var changed = state != State || problem != Problem;
        (State, Problem) = (state, problem);
        if (changed || state == "online") StateChanged?.Invoke(this);
    }

    /// <summary>Waits (at most <paramref name="wait"/>) while it's connecting: a request made then goes once it's connected, or fails with why not.</summary>
    public async Task WhenSettledAsync(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (State == "connecting" && DateTime.UtcNow < until) await Task.Delay(150);
    }

    /// <summary>Sends the server's host a message from the page, as it is (no reply wanted).</summary>
    public void Send(string json)
    {
        if (link is not { IsOpen: true } l) throw NotReachable();
        l.Send(LinkKind.Page, json);
    }

    /// <summary>Sends the server's host a request; the task ends with its result, or its error.</summary>
    public Task<JsonElement> RequestAsync(JsonObject message)
    {
        if (link is not { IsOpen: true } l) return Task.FromException<JsonElement>(NotReachable());
        var id = Interlocked.Increment(ref requests);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        replies[id] = reply;
        message["reqId"] = id;
        l.Send(LinkKind.Page, message.ToJsonString());
        return reply.Task.WaitAsync(TimeSpan.FromMinutes(5)).ContinueWith(t =>
        {
            replies.TryRemove(id, out _);
            return t.GetAwaiter().GetResult();
        }, TaskScheduler.Default);
    }

    public Task<JsonElement> RequestAsync(object message) => RequestAsync(JsonSerializer.SerializeToNode(message, JsonOptions)!.AsObject());

    private Exception NotReachable() => new InvalidOperationException(State switch
    {
        "elsewhere" => $"{Target}'s sessions are showing in another LlamaTree window.",
        "connecting" => $"Still connecting to {Target}.",
        _ => $"{Target} can't be reached right now{(Problem is { Length: > 0 } p ? ": " + p : ".")}",
    });

    private bool TakeReply(string json)
    {
        if (!json.StartsWith("""{"type":"reply",""", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            if (!m.TryGetProperty("reqId", out var r) || !r.TryGetInt32(out var id) || !replies.TryRemove(id, out var reply)) return true;
            if (m.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                reply.TrySetResult(m.TryGetProperty("result", out var result) ? result.Clone() : default);
            else reply.TrySetException(new InvalidOperationException(Str(m, "error")));
        }
        catch (JsonException) { }
        return true;
    }

    public void Dispose()
    {
        disposed = true;
        link?.Close();
        pending?.Close();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : -1;
}
