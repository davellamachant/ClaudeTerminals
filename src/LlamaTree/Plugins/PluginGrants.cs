using System.Text.Json;
using LlamaTree.Relay;

namespace LlamaTree.Plugins;

/// <summary>
/// What you decided about one plugin: the kinds of access you allowed and refused, the projects whose sessions it sees
/// (null: every project, including ones added later), the sessions you hid from it one by one, and the plugin.json it had
/// when you last allowed something (<see cref="Manifest"/>).
/// </summary>
/// <param name="Changed">Its plugin.json changed since you allowed it, so what you allowed is asked again.</param>
internal sealed record PluginGrant(List<string> Allowed, List<string> Denied, List<string>? Projects, List<string> Hidden, string? Manifest, bool Changed = false)
{
    public static PluginGrant Empty => new([], [], null, [], null);
}

/// <summary>
/// Your answers about plugins, kept in plugin-grants.json: the one place they're kept, so the page (which hands plugins
/// what they ask for) and the relay (which carries their messages) go by the same answers. A kind in neither Allowed nor
/// Denied is asked about when a plugin first needs it. Thread safe.
/// </summary>
internal sealed class PluginGrants
{
    /// <summary>The project key of sessions with no project.</summary>
    public const string NoProject = "none";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string? path;
    private readonly Lock gate = new();
    private readonly Dictionary<string, PluginGrant> grants;

    /// <param name="path">Where they're kept; null keeps them in memory only (tests).</param>
    public PluginGrants(string? path)
    {
        this.path = path;
        grants = path == null ? [] : Load(path);
    }

    /// <summary>Raised after any change, on whichever thread made it.</summary>
    public event Action? Changed;

    public Dictionary<string, PluginGrant> Snapshot()
    {
        lock (gate) return new(grants);
    }

    public bool IsEmpty
    {
        get { lock (gate) return grants.Count == 0; }
    }

    /// <summary>"allow", "deny" or "ask".</summary>
    public string State(string id, string kind)
    {
        lock (gate)
        {
            var g = grants.GetValueOrDefault(id);
            return g == null ? "ask" : g.Allowed.Contains(kind) ? "allow" : g.Denied.Contains(kind) ? "deny" : "ask";
        }
    }

    /// <summary>Sets what plugin <paramref name="id"/> may do with <paramref name="kind"/>; allowing it records <paramref name="manifest"/>.</summary>
    public void Set(string id, string kind, string state, string? manifest)
    {
        if (state is not ("allow" or "deny" or "ask")) throw new ArgumentException($"There's no answer \"{state}\".");
        Update(id, g =>
        {
            var allowed = g.Allowed.Where(k => k != kind).ToList();
            var denied = g.Denied.Where(k => k != kind).ToList();
            if (state == "allow") allowed.Add(kind);
            if (state == "deny") denied.Add(kind);
            return g with { Allowed = allowed, Denied = denied, Manifest = state == "allow" ? manifest ?? g.Manifest : g.Manifest, Changed = state == "allow" ? false : g.Changed };
        });
    }

    /// <summary>The projects whose sessions it sees: project ids and <see cref="NoProject"/>, or null for every project.</summary>
    public void SetProjects(string id, IEnumerable<string>? projects) =>
        Update(id, g => g with { Projects = projects?.Distinct().ToList() });

    public void SetHidden(string id, string session, bool hidden) =>
        Update(id, g => g with { Hidden = hidden ? [.. g.Hidden.Where(s => s != session), session] : g.Hidden.Where(s => s != session).ToList() });

    public void Forget(string id)
    {
        lock (gate)
        {
            if (!grants.Remove(id)) return;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Whether the plugin sees a session (by its id and project key): in its projects, and not hidden from it. Says
    /// nothing about whether it's allowed the access itself.
    /// </summary>
    public bool Sees(string id, string session, string projectKey)
    {
        lock (gate)
        {
            var g = grants.GetValueOrDefault(id);
            return g == null || ((g.Projects == null || g.Projects.Contains(projectKey)) && !g.Hidden.Contains(session));
        }
    }

    /// <summary>How a plugin is named among the relay's peers: "plugin:" and its id.</summary>
    public const string PeerPrefix = "plugin:";

    /// <summary>
    /// The relay's rules for plugins, in place of its own rules for them (<see cref="RelayHub.Gate"/>): a plugin may message
    /// a session, and a session it, when you allowed it "message" access and it sees that session (its projects, less
    /// sessions you hid). A session may message a plugin first only if the plugin takes messages ("inbox": true); a reply
    /// always may. Two plugins never message each other. Null for messages between sessions, which follow the relay's rules.
    /// </summary>
    public RelayDecision? RelayGate(RelayPeer from, RelayPeer to, bool isReply)
    {
        var fromPlugin = from.Id.StartsWith(PeerPrefix, StringComparison.Ordinal);
        var toPlugin = to.Id.StartsWith(PeerPrefix, StringComparison.Ordinal);
        if (!fromPlugin && !toPlugin) return null;
        if (fromPlugin && toPlugin) return RelayDecision.Deny;
        var plugin = (fromPlugin ? from : to).Id[PeerPrefix.Length..];
        var session = fromPlugin ? to : from;
        if (toPlugin && !to.Inbox && !isReply) return RelayDecision.Deny;
        return State(plugin, "message") == "allow" && Sees(plugin, session.Id, session.Project ?? NoProject) ? RelayDecision.Allow : RelayDecision.Deny;
    }

    /// <summary>
    /// Checks each plugin's plugin.json against the one it had when you allowed it: one that changed has what you allowed
    /// taken back (asked again, marked <see cref="PluginGrant.Changed"/>). Returns the ids of those it took back.
    /// </summary>
    public List<string> Reconcile(IEnumerable<PluginInfo> plugins)
    {
        var changed = new List<string>();
        lock (gate)
        {
            var dirty = false;
            foreach (var p in plugins)
            {
                if (!grants.TryGetValue(p.Id, out var g) || g.Allowed.Count == 0 || g.Manifest == p.ManifestHash) continue;
                dirty = true;
                if (g.Manifest == null) { grants[p.Id] = g with { Manifest = p.ManifestHash }; continue; }   // allowed before hashes were kept
                grants[p.Id] = g with { Allowed = [], Changed = true };
                changed.Add(p.Id);
            }
            if (dirty) Save();
        }
        if (changed.Count > 0) Changed?.Invoke();
        return changed;
    }

    /// <summary>Drops sessions and projects since removed from what plugins see.</summary>
    public void Keep(IReadOnlySet<string> sessions, IReadOnlySet<string> projects)
    {
        var any = false;
        lock (gate)
        {
            foreach (var (id, g) in grants.ToList())
            {
                var hidden = g.Hidden.Where(sessions.Contains).ToList();
                var proj = g.Projects?.Where(k => k == NoProject || projects.Contains(k)).ToList();
                if (hidden.Count == g.Hidden.Count && (proj?.Count ?? -1) == (g.Projects?.Count ?? -1)) continue;
                grants[id] = g with { Hidden = hidden, Projects = proj };
                any = true;
            }
            if (any) Save();
        }
        if (any) Changed?.Invoke();
    }

    /// <summary>Takes in answers the page kept before they were kept here (only into an empty store, once).</summary>
    public void Import(Dictionary<string, PluginGrant> from)
    {
        lock (gate)
        {
            if (grants.Count > 0) return;
            foreach (var (id, g) in from)
                if (!string.IsNullOrEmpty(id))
                    grants[id] = new PluginGrant(g.Allowed ?? [], g.Denied ?? [], g.Projects, g.Hidden ?? [], null);
            Save();
        }
        Changed?.Invoke();
    }

    private void Update(string id, Func<PluginGrant, PluginGrant> change)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Which plugin?");
        lock (gate)
        {
            var g = change(grants.GetValueOrDefault(id) ?? PluginGrant.Empty);
            if (g.Allowed.Count == 0 && g.Denied.Count == 0 && g.Projects == null && g.Hidden.Count == 0 && !g.Changed) grants.Remove(id);
            else grants[id] = g;
            Save();
        }
        Changed?.Invoke();
    }

    private static Dictionary<string, PluginGrant> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, PluginGrant>>(File.ReadAllText(path), Options) is { } read)
                return read.Where(x => !string.IsNullOrEmpty(x.Key) && x.Value != null)
                    .ToDictionary(x => x.Key, x => x.Value with { Allowed = x.Value.Allowed ?? [], Denied = x.Value.Denied ?? [], Hidden = x.Value.Hidden ?? [] });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file loses the answers: every plugin asks again, which is the safe way round.
        }
        return [];
    }

    private void Save()
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(grants, Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this run; written next time something changes.
        }
    }
}
