using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaTree.Hosting;
using LlamaTree.Sessions;

namespace LlamaTree;

/// <summary>
/// Sessions that run on servers, shown among this computer's own. Each server's LlamaTree runs its sessions; this host
/// keeps a connection to it (<see cref="ServerConnection"/>, over ssh), and is a window to it: what that host tells its
/// page about a session (its output, Git status…) is passed to this page under the session's id here, and what this
/// page asks about the session is passed to that host under its id there. A session keeps its place here (among the
/// others, in a project) and what it was last seen as, so it's shown while its server can't be reached.
/// <para>
/// Every session a server's LlamaTree has is shown, whichever window added it. A request naming a server rather than a
/// session (adding sessions there, browsing its folders, its saved conversations) goes to that server.
/// </para>
/// </summary>
internal sealed partial class AppHost
{
    private sealed class ServerSession(SessionInfo info)
    {
        public SessionInfo Info { get; } = info;
        public bool Running { get; set; }

        /// <summary>The repository its Git tabs show, on its server (from its last Git status), or null.</summary>
        public string? RepoRoot { get; set; }
    }

    /// <summary>Sessions on servers, by their id here.</summary>
    private readonly Dictionary<string, ServerSession> serverSessions = [];

    /// <summary>The connection to each server with sessions here (or asked about), by how it's reached.</summary>
    private readonly Dictionary<string, ServerConnection> servers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The project for sessions a server is adding at this page's request, until it has answered.</summary>
    private readonly Dictionary<string, string?> addingTo = new(StringComparer.OrdinalIgnoreCase);

    private void AddServerSession(SessionInfo info)
    {
        serverSessions[info.Id] = new ServerSession(info);
        order.Add(info.Id);
        ServerFor(info.Server!);
    }

    /// <summary>The connection to <paramref name="target"/>, made (and connecting) the first time it's needed.</summary>
    private ServerConnection ServerFor(string target)
    {
        if (servers.TryGetValue(target, out var c)) return c;
        if (!Ssh.IsValidTarget(target)) throw new ArgumentException("Enter a server as user@server, or a Host from your SSH config.");
        if (Remote != null) throw new InvalidOperationException("A window showing a server's sessions can't add sessions from other servers.");
        c = new ServerConnection(target, RunOnUi);
        c.PageMessage += OnServerMessage;
        c.ToolCall += OnServerToolCall;
        c.PluginsChanged += _ => Post(new { type = "plugins.changed" });
        c.FileOpened += (_, name) => Post(new { type = "files.opened", name, server = c.Machine is { Length: > 0 } m ? m : c.Target });
        c.StateChanged += _ => Post(new { type = "servers", servers = ServersState() });
        servers[target] = c;
        c.Connect();
        return c;
    }

    /// <summary>Each server's connection, for the page: how it's going, and why not when it isn't.</summary>
    private object ServersState() => servers.Values.Select(c => new
    {
        target = c.Target, state = c.State, problem = c.Problem, machine = c.Machine, version = c.Version,
        // Older than this computer's LlamaTree: it can be updated to match (servers.update).
        outdated = Version.TryParse(c.Version, out var v) && Updates.UpdateService.Normalize(v) < Updates.UpdateService.CurrentVersion,
        current = Updates.UpdateService.CurrentVersion.ToString(),
    });

    /// <summary>The page has loaded: each server's host is asked again for its sessions and what they show.</summary>
    private void ServersPageReady()
    {
        foreach (var c in servers.Values.Where(c => c.IsOnline)) c.Send("""{"type":"ready"}""");
    }

    private void DisposeServers()
    {
        foreach (var c in servers.Values) c.Dispose();
    }

    /// <summary>
    /// A request about a session on a server, or naming a server, sent there: (true, its answer). (false, _) for one
    /// this computer answers.
    /// </summary>
    private async Task<(bool Handled, object? Result)> TryForwardAsync(string type, JsonElement msg)
    {
        if (OptStr(msg, "id") is { } id && serverSessions.TryGetValue(id, out var ss))
        {
            var c = ServerFor(ss.Info.Server!);
            switch (type)
            {
                case "updateSession":
                    // Its project is kept here; the rest (name, command, conversation) is the server's.
                    if (msg.TryGetProperty("project", out _))
                    {
                        ss.Info.Project = ProjectId(msg);
                        SaveSessions();
                        Post(SessionList());
                    }
                    if (!msg.TryGetProperty("name", out _) && !msg.TryGetProperty("command", out _) && !msg.TryGetProperty("conversation", out _)) return (true, null);
                    break;
                case "removeSession":
                    // Removed there too. A server that can't be reached keeps it, and shows it again once it can be.
                    if (c.IsOnline) await ForwardAsync(c, msg, ss.Info.RemoteId);
                    ForgetServerSession(ss);
                    ForgetPanels(ss.Info.Id);
                    SaveSessions();
                    Post(SessionList());
                    return (true, null);
                case "files.upload":
                    return (true, await UploadToAsync(c, msg));
            }
            var answer = await ForwardAsync(c, msg, ss.Info.RemoteId);
            if (type is "start" or "restart" && !ss.Running)
            {
                // Its server sends its session list only when sessions change, not when one starts: it runs now, as far
                // as sessions here are told (list_sessions, stop_session…), until it says it exited.
                ss.Running = true;
                relay.SetPeers(RelayPeers());
            }
            return (true, answer);
        }
        if (OptStr(msg, "server") is { Length: > 0 } server)
        {
            var c = ServerFor(server);
            switch (type)
            {
                case "servers.reconnect":
                    c.Reconnect();
                    return (true, null);
                case "servers.update":
                {
                    // Installed first; then its LlamaTree restarts on it (the page has asked you, as that stops its sessions).
                    var said = await ServerUpdate.UpdateAsync(c.Target, Updates.UpdateService.CurrentVersion);
                    c.Restart();
                    return (true, said);
                }
                case "addSessions":
                {
                    // Sessions it adds show up in its session list (before it answers): they go in the project picked here.
                    var first = msg.GetProperty("sessions").EnumerateArray().FirstOrDefault();
                    addingTo[c.Target] = first.ValueKind == JsonValueKind.Object ? ProjectId(first) : null;
                    try { return (true, await ForwardAsync(c, msg, null)); }
                    finally { addingTo.Remove(c.Target); }
                }
            }
            return (true, await ForwardAsync(c, msg, null));
        }
        return (false, null);
    }

    /// <summary>
    /// Sends a page's message on to a server, about its session <paramref name="remoteId"/> (when it's about one), and
    /// waits for the answer if the page wants one.
    /// </summary>
    private static async Task<object?> ForwardAsync(ServerConnection c, JsonElement msg, string? remoteId)
    {
        if (c.State == "connecting") await c.WhenSettledAsync(TimeSpan.FromSeconds(30));
        var obj = JsonNode.Parse(msg.GetRawText())!.AsObject();
        var wantsReply = obj.Remove("reqId");
        obj.Remove("server");
        if (remoteId != null) obj["id"] = remoteId;
        if (obj["type"]?.GetValue<string>() == "updateSession") obj.Remove("project");
        if (obj["sessions"] is JsonArray items)
            foreach (var item in items.OfType<JsonObject>()) item.Remove("project");   // projects are this computer's
        if (!wantsReply)
        {
            c.Send(obj.ToJsonString());
            return null;
        }
        var result = await c.RequestAsync(obj);
        return result.ValueKind == JsonValueKind.Undefined ? null : result;
    }

    /// <summary>Files on this computer (pasted into a server's session) copied to the server: their paths there.</summary>
    private async Task<object?> UploadToAsync(ServerConnection c, JsonElement msg)
    {
        if (window.ProcessId == null) throw new InvalidOperationException("Pasting files into a session on another server isn't possible from here.");
        var paths = msg.TryGetProperty("paths", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
        if (c.State == "connecting") await c.WhenSettledAsync(TimeSpan.FromSeconds(30));
        return await ServerFiles.UploadAsync(paths, m => c.RequestAsync(m), c.Send);
    }

    /// <summary>
    /// A server session's call to a tool (show_panel, send_message…), answered here as for a session here: its panels
    /// show here, and its messages go to and from the sessions here.
    /// </summary>
    private async void OnServerToolCall(ServerConnection c, int id, string remoteId, JsonElement message)
    {
        JsonObject? reply;
        try
        {
            reply = FindServerSession(c.Target, remoteId) is { } ss && mcp != null
                ? await mcp.HandleAsync(ss.Info.Id, message)
                : Panels.PanelMcp.Error(message.TryGetProperty("id", out var rid) ? JsonNode.Parse(rid.GetRawText()) : null, -32603, "LlamaTree doesn't know this session.");
        }
        catch (Exception ex)
        {
            reply = Panels.PanelMcp.Error(null, -32603, ex.Message);
        }
        try { c.Send(JsonSerializer.Serialize(new { type = "tools.reply", id, reply }, JsonOptions)); }
        catch (InvalidOperationException) { /* the connection went: the server answers it itself */ }
    }

    /// <summary>
    /// Connected servers' plugins, among this computer's: each served here from the copy its server sent, under an id
    /// of its own (its id there and its server's, so it never meets a plugin of the same name here), and with its
    /// server's name after its title. Like any plugin here, it sees what you allow it to: sessions here and on servers.
    /// </summary>
    private void AddServerPlugins(List<Plugins.PluginInfo> found, List<string> problems)
    {
        foreach (var c in servers.Values)
        {
            var (list, trouble) = c.ServerPlugins();
            var key = HostLink.RemoteKey(c.Target)[..8].ToLowerInvariant();
            found.AddRange(list.Select(p => p with { Id = $"{p.Id[..Math.Min(p.Id.Length, 31)]}-{key}", Title = $"{p.Title} ({c.Target})" }));
            problems.AddRange(trouble.Select(t => $"{c.Target}: {t}"));
        }
    }

    private ServerSession? FindServerSession(string server, string? remoteId) =>
        remoteId == null ? null : serverSessions.Values.FirstOrDefault(x => x.Info.RemoteId == remoteId && string.Equals(x.Info.Server, server, StringComparison.OrdinalIgnoreCase));

    private void ForgetServerSession(ServerSession ss)
    {
        serverSessions.Remove(ss.Info.Id);
        order.Remove(ss.Info.Id);
    }

    /// <summary>
    /// What a server's host told its page (this host): its sessions, or something about one of them, passed to this
    /// page under the session's id here. The rest (its panels, plugins, theme…) is the server's own business.
    /// </summary>
    private void OnServerMessage(ServerConnection c, string json)
    {
        JsonObject obj;
        try { obj = JsonNode.Parse(json)!.AsObject(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return; }
        var type = obj["type"]?.GetValue<string>();
        switch (type)
        {
            case "sessions":
                if (obj["sessions"] is JsonArray list) SyncServerSessions(c, list);
                break;
            case "output":
                if (!webReady) break;   // no page: the server keeps it, and replays it when this page loads
                goto case "exited";
            case "replay":
            case "exited":
            case "git.status":
            case "typePrompt":
            {
                if (FindServerSession(c.Target, obj["id"]?.GetValue<string>()) is not { } ss) break;
                obj["id"] = ss.Info.Id;
                if (type == "exited" && ss.Running) { ss.Running = false; relay.SetPeers(RelayPeers()); }
                if (type == "git.status") ss.RepoRoot = obj["root"] is JsonValue root && root.TryGetValue<string>(out var r) ? r : null;
                window.PostJson(obj.ToJsonString());
                break;
            }
            case "newConversations":
            {
                var ids = (obj["ids"] as JsonArray ?? []).Select(x => FindServerSession(c.Target, x?.GetValue<string>())?.Info.Id).OfType<string>().ToList();
                if (ids.Count > 0) Post(new { type = "newConversations", ids });
                break;
            }
            case "files.browse":
                obj["server"] = c.Target;
                window.PostJson(obj.ToJsonString());
                break;
        }
    }

    /// <summary>
    /// A server's sessions as it has them now: new ones are shown (at the end, or in the project they're being added
    /// to), gone ones go, and each one's name, folder, command and whether it's running are taken from there.
    /// </summary>
    private void SyncServerSessions(ServerConnection c, JsonArray list)
    {
        var seen = new HashSet<string>();
        var save = false;
        foreach (var item in list.OfType<JsonObject>())
        {
            if (item["id"]?.GetValue<string>() is not { Length: > 0 } rid) continue;
            seen.Add(rid);
            var ss = FindServerSession(c.Target, rid);
            if (ss == null)
            {
                var info = new SessionInfo { Server = c.Target, RemoteId = rid, Project = addingTo.GetValueOrDefault(c.Target) };
                AddServerSession(info);
                ss = serverSessions[info.Id];
                save = true;
            }
            var i = ss.Info;
            string Text(string name) => item[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            string? Maybe(string name) => item[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (i.Name != Text("name") || i.Directory != Text("directory") || i.Command != Text("command") || i.Conversation != Maybe("conversation"))
            {
                (i.Name, i.Directory, i.Command, i.Conversation) = (Text("name"), Text("directory"), Text("command"), Maybe("conversation"));
                save = true;
            }
            ss.Running = item["running"] is JsonValue r && r.TryGetValue<bool>(out var running) && running;
        }
        foreach (var gone in serverSessions.Values.Where(x => string.Equals(x.Info.Server, c.Target, StringComparison.OrdinalIgnoreCase) && !seen.Contains(x.Info.RemoteId!)).ToList())
        {
            ForgetServerSession(gone);
            save = true;
        }
        if (save) SaveSessions();
        Post(SessionList());
    }
}
