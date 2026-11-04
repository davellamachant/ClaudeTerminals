using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaTree.Relay;

/// <summary>What a session may do to other sessions, beyond messaging them (see <see cref="SessionControl"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControlAbility>))]
internal enum ControlAbility
{
    /// <summary>Start new sessions (which it then controls). With no rule, you're asked each time.</summary>
    Spawn,

    /// <summary>Answer the permission prompts of the sessions it started. With no rule, it may.</summary>
    Answer,
}

/// <summary>
/// A rule for one ability. <see cref="Scope"/> is a session id, "project:&lt;id&gt;" for every session in a project, or
/// <see cref="ControlPolicy.Everyone"/>.
/// </summary>
internal sealed record ControlRule(ControlAbility Ability, string Scope, RelayDecision Decision);

/// <summary>
/// Which session may start sessions and answer their prompts, kept in control.json. The most specific rule decides: the
/// session's own, then its project's, then everyone's; with none, starting a session asks you and answering prompts is
/// allowed. A session that was itself started by another goes only by its own rule for starting sessions, so a project
/// rule doesn't let sessions multiply: each generation needs your say. Thread safe.
/// </summary>
internal sealed class ControlPolicy
{
    public const string Everyone = "*";
    public const string ProjectPrefix = "project:";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string? path;
    private readonly Lock gate = new();
    private readonly List<ControlRule> rules;

    /// <param name="path">Where the rules are kept; null keeps them in memory only (tests).</param>
    public ControlPolicy(string? path)
    {
        this.path = path;
        rules = path == null ? [] : Load(path);
    }

    /// <summary>Raised after any change, on whichever thread made it.</summary>
    public event Action? Changed;

    public static RelayDecision Default(ControlAbility ability) => ability == ControlAbility.Spawn ? RelayDecision.Ask : RelayDecision.Allow;

    public List<ControlRule> Rules()
    {
        lock (gate) return [.. rules];
    }

    /// <summary>What <paramref name="session"/> gets for <paramref name="ability"/>, and the rule that says so (null: none).</summary>
    /// <param name="startedByAnother">The session was started by another session (see the class summary).</param>
    public (RelayDecision Decision, ControlRule? Rule) Decide(ControlAbility ability, string session, string? project, bool startedByAnother = false)
    {
        lock (gate)
        {
            ControlRule? Find(string scope) => rules.FirstOrDefault(r => r.Ability == ability && r.Scope == scope);
            var rule = Find(session);
            if (rule == null && !(startedByAnother && ability == ControlAbility.Spawn))
                rule = (project != null ? Find(ProjectPrefix + project) : null) ?? Find(Everyone);
            return (rule?.Decision ?? Default(ability), rule);
        }
    }

    /// <summary>Sets the rule; <see cref="RelayDecision.Ask"/> removes it, so the next one down decides.</summary>
    public void Set(ControlAbility ability, string scope, RelayDecision decision)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope == ProjectPrefix) throw new ArgumentException("A rule is for a session, \"project:<id>\" or \"*\".");
        lock (gate)
        {
            rules.RemoveAll(r => r.Ability == ability && r.Scope == scope);
            if (decision != RelayDecision.Ask) rules.Add(new ControlRule(ability, scope, decision));
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Drops the rules of sessions and projects no longer there.</summary>
    public void Keep(IEnumerable<string> sessions, IEnumerable<string> projects)
    {
        var keep = sessions.Concat(projects.Select(p => ProjectPrefix + p)).Append(Everyone).ToHashSet();
        lock (gate)
        {
            if (rules.RemoveAll(r => !keep.Contains(r.Scope)) == 0) return;
            Save();
        }
        Changed?.Invoke();
    }

    private static List<ControlRule> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<ControlRule>>(File.ReadAllText(path), Options) is { } list)
                return list.Where(r => !string.IsNullOrWhiteSpace(r.Scope) && r.Decision != RelayDecision.Ask)
                    .DistinctBy(r => (r.Ability, r.Scope)).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file loses the rules: starting sessions asks again, which is the safe way round.
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
            File.WriteAllText(temp, JsonSerializer.Serialize(rules, Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this run; written next time something changes.
        }
    }
}
