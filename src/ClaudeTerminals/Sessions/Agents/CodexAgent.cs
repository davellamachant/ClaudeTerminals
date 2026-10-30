using System.Text.RegularExpressions;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Sessions;

/// <summary>
/// OpenAI's Codex CLI. Codex can't be told which id a new conversation gets, so a session learns it from the rollout its
/// Codex writes (see <see cref="CodexConversations.Live"/>) and reopens it with codex resume. Panels are added as an
/// MCP server on its command line, with the token in its environment.
/// </summary>
internal sealed partial class CodexAgent : AgentProfile
{
    public override string Id => "codex";
    public override string Name => "Codex";
    public override IReadOnlyList<string> Programs { get; } = ["codex", "codex.exe", "codex.cmd"];
    public override AgentColor Color { get; } = new("#4a9eff", "#1a66c9");
    public override bool ChoosesConversationId => false;
    public override string Follows => "through /new and /resume";
    public override string OwnConversationPattern => @"\s+(?:resume|fork)(?:\s+--last|\s+[^-\s]\S*)?(?=\s|$)";

    /// <summary>
    /// Codex reports when it's waiting on you (OSC 7501 state=blocked) for approvals and questions, but not for the folder
    /// trust check it can show at start, before it reports anything: "› 1. Trust and continue". Its prompt box starts
    /// with "› " too, so only that choice counts.
    /// </summary>
    public override AskingScreen Asks { get; } = new(@"^\s*› ?1\. Trust and continue\b");

    /// <summary>
    /// codex exec reading the prompt from standard input ("-") and printing only its answer: not saved (--ephemeral), in a
    /// read-only sandbox, with little reasoning (it's only a summary). Codex has no switch to take its tools away; the
    /// prompt carries the diff, so it has no need of them.
    /// </summary>
    public override string HeadlessCommand => "codex exec --ephemeral -s read-only --skip-git-repo-check --color never -c model_reasoning_effort=low -";

    /// <summary>The environment variable Codex reads its panel token from (so it isn't on the command line).</summary>
    internal const string PanelTokenVariable = "CLAUDE_TERMINALS_PANEL_TOKEN";

    /// <summary>Codex's subcommands: anything but the interactive interface, except resume and fork (which pick their own conversation).</summary>
    private static readonly HashSet<string> Subcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "exec", "e", "review", "login", "logout", "mcp", "mcp-server", "plugin", "app-server", "remote-control", "app", "completion",
        "update", "doctor", "sandbox", "debug", "apply", "a", "resume", "queue", "archive", "delete", "migrate-rollouts", "unarchive",
        "fork", "cloud", "exec-server", "features", "help", "agents",
    };

    /// <summary>The subcommand a codex command runs (lowercase), or null for the interactive interface.</summary>
    private static string? Subcommand(string command) => Tokens(command).Skip(1).FirstOrDefault(Subcommands.Contains)?.ToLowerInvariant();

    public override bool CanTrackConversation(string command) => Subcommand(command) == null;

    public override bool CarriesOnConversation(string command) => Subcommand(command) == "resume";

    /// <summary>"resume &lt;id&gt;".</summary>
    public override (string Command, string? Conversation) TakeConversation(string command)
    {
        var m = Resume().Match(command);
        if (!m.Success || !TerminalSession.IsConversationId(m.Groups[1].Value)) return (command, null);
        var rest = (command[..m.Index] + command[(m.Index + m.Length)..]).Trim();
        return CanTrackConversation(rest) ? (rest, m.Groups[1].Value) : (command, null);
    }

    [GeneratedRegex(@"\s+resume\s+([0-9A-Fa-f-]{36})(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Resume();

    /// <summary>
    /// Resumes the session's conversation, or for a thread Codex started under one (a Guardian review a session could
    /// take for its conversation before sub-threads were told apart), that conversation.
    /// </summary>
    public override string CommandLine(string command, Launch launch) =>
        PrepareCommand(command, (launch.Conversation ?? launch.ResumeOnce) is { } id ? CodexConversations.Resumable(id) : null,
            CodexConversations.IsSaved, launch.Panels?.Url, launch.NewConversation, launch.FirstPrompt);

    public override void AddEnvironment(IDictionary<string, string> env, Launch launch)
    {
        if (launch.Panels != null) env[PanelTokenVariable] = launch.Panels.Token;
    }

    /// <summary>
    /// The command line for this start of a codex session:
    /// - with a <paramref name="conversation"/> Codex has saved (<paramref name="isSaved"/>): codex resume it;
    /// - otherwise a new conversation, which the app learns once Codex has written it.
    /// A command with a subcommand of its own (codex resume --last, codex exec, …) keeps its choice.
    /// </summary>
    /// <param name="panelUrl">
    /// The app's panel server, added as an MCP server (its token is read from <see cref="PanelTokenVariable"/>) unless the
    /// command configures its own.
    /// </param>
    /// <param name="firstPrompt">The prompt a new conversation starts with: Codex's prompt argument, straight after the program.</param>
    internal static string PrepareCommand(string command, string? conversation, Func<string, bool> isSaved, string? panelUrl = null,
        bool newConversation = false, string? firstPrompt = null)
    {
        if (Agents.For(command) is not CodexAgent) return command;
        var subcommand = Subcommand(command);
        if (subcommand is not (null or "resume" or "fork")) return command;   // not the interactive interface

        var result = command.Trim();
        if (subcommand == null)
        {
            if (!newConversation && TerminalSession.IsConversationId(conversation) && isSaved(conversation!))
                result = AfterProgram(result, "resume " + conversation);
            else if (firstPrompt != null)
                result = AfterProgram(result, CommandLineResolver.QuoteArgument(firstPrompt));
        }
        var server = "mcp_servers." + Panels.PanelMcp.ServerName;
        if (panelUrl != null && !result.Contains(server, StringComparison.OrdinalIgnoreCase))
            // Neither value is valid TOML, so Codex takes each as the literal text.
            result = result.TrimEnd() + $" -c {server}.url={panelUrl} -c {server}.bearer_token_env_var={PanelTokenVariable}";
        return result;
    }

    /// <summary>The latest one started in the session's folder since the session started, unless another session is in it.</summary>
    public override string? LiveConversation(TerminalSession session, int[] processIds, IReadOnlySet<string> others) =>
        CodexConversations.Live(session.Info.Directory, session.StartedUtc, others);

    public override List<ConversationInfo> ListConversations(string? directory, int max) => CodexConversations.List(directory, max);

    public override ConversationInfo? FindConversation(string id, string directory) => CodexConversations.Find(id);

    public override bool ShowsConversation => true;

    /// <summary>The session's own conversation, which the app follows (Codex names no conversation for its process).</summary>
    public override Conversation? ReadConversation(TerminalSession session, int[] processIds) =>
        session.Info.Conversation is { } id ? CodexConversations.Read(id) : null;
}
