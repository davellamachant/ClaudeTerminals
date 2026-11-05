using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaTree.Relay;

/// <summary>
/// A rule about sessions messaging others: <see cref="From"/> is a session id, or <see cref="RelayPolicy.Anyone"/> for
/// every session (the global level); <see cref="To"/> is a session id,
/// <see cref="RelayPolicy.SameProject"/> (any session in its project) or <see cref="RelayPolicy.Anyone"/> (any session).
/// </summary>
internal sealed record RelayRule(string From, string To, RelayDecision Decision);

[JsonConverter(typeof(JsonStringEnumConverter<RelayDecision>))]
internal enum RelayDecision { Ask, Allow, Deny }

/// <summary>
/// Which session may message which, kept in relay.json. A message from A to B follows the most specific rule A has:
/// for B itself, then for any session in A's project (when B is in it), then for any session; then the same two for every
/// session (the global rules, from <see cref="Anyone"/>); with none, you're asked.
/// Rules go one way: A may message B says nothing about B messaging A. Thread safe.
/// </summary>
internal sealed class RelayPolicy
{
    public const string SameProject = "project";
    public const string Anyone = "*";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string? path;
    private readonly Lock gate = new();
    private List<RelayRule> rules;
    private bool paused;

    /// <param name="path">Where the rules are kept; null keeps them in memory only (tests).</param>
    public RelayPolicy(string? path)
    {
        this.path = path;
        (rules, paused) = path == null ? (new List<RelayRule>(), false) : Load(path);
    }

    /// <summary>Raised after any change, on whichever thread made it.</summary>
    public event Action? Changed;

    /// <summary>While paused no session can message another, whatever the rules say.</summary>
    public bool Paused
    {
        get { lock (gate) return paused; }
        set
        {
            lock (gate)
            {
                if (paused == value) return;
                paused = value;
                Save();
            }
            Changed?.Invoke();
        }
    }

    public List<RelayRule> Rules()
    {
        lock (gate) return [.. rules];
    }

    /// <summary>What a message from <paramref name="from"/> to <paramref name="to"/> gets, and the rule that says so (null: none, so ask).</summary>
    public (RelayDecision Decision, RelayRule? Rule) Decide(string from, string to, bool sameProject)
    {
        lock (gate)
        {
            RelayRule? Find(string f, string t) => rules.FirstOrDefault(r => r.From == f && r.To == t);
            var rule = Find(from, to)
                ?? (sameProject ? Find(from, SameProject) : null)
                ?? Find(from, Anyone)
                ?? (sameProject ? Find(Anyone, SameProject) : null)
                ?? Find(Anyone, Anyone);
            return (rule?.Decision ?? RelayDecision.Ask, rule);
        }
    }

    /// <summary>Sets the rule from → to; <see cref="RelayDecision.Ask"/> removes it, so the next one down decides.</summary>
    public void Set(string from, string to, RelayDecision decision)
    {
        if (!Valid(from, to)) throw new ArgumentException("A rule is from a session (or every session, \"*\") to another session, \"project\" or \"*\".");
        lock (gate)
        {
            rules.RemoveAll(r => r.From == from && r.To == to);
            if (decision != RelayDecision.Ask) rules.Add(new RelayRule(from, to, decision));
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Drops every rule from or to sessions no longer in <paramref name="sessions"/>.</summary>
    public void KeepSessions(IEnumerable<string> sessions)
    {
        var keep = sessions.ToHashSet();
        lock (gate)
        {
            if (rules.RemoveAll(r => (r.From != Anyone && !keep.Contains(r.From)) || (r.To is not (SameProject or Anyone) && !keep.Contains(r.To))) == 0) return;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>From a session or every session; to another session, a project or anyone (every session to one session isn't a rule).</summary>
    private static bool Valid(string? from, string? to) =>
        !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to) && from != SameProject && (from == Anyone ? to is SameProject or Anyone : from != to);

    private sealed record Saved(bool Paused, List<RelayRule> Rules);

    private static (List<RelayRule>, bool) Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Options) is { } s)
                return (s.Rules.Where(r => Valid(r.From, r.To) && r.Decision != RelayDecision.Ask)
                    .DistinctBy(r => (r.From, r.To)).ToList(), s.Paused);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file loses the rules: every session asks again, which is the safe way round.
        }
        return ([], false);
    }

    private void Save()
    {
        if (path == null) return;
        try
        {
            Platform.PrivateFiles.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Saved(paused, rules), Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this run; written next time something changes.
        }
    }
}
