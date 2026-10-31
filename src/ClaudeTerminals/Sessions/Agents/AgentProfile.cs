namespace ClaudeTerminals.Sessions;

/// <summary>What a session's start knows, for its agent to build the command line from.</summary>
/// <param name="Conversation">The session's own conversation (<see cref="SessionInfo.Conversation"/>).</param>
/// <param name="ResumeOnce">After an update, the conversation the session was in (see <see cref="TerminalSession.ResumeOnce"/>).</param>
/// <param name="Theme">"light" or "dark": the agent is started in colours that match the terminal, if it can be.</param>
/// <param name="Panels">How the agent reaches the app's panel server, or null when panels are unavailable.</param>
/// <param name="NewConversation">Start a new conversation (a session started from a panel), whatever the session was in.</param>
/// <param name="FirstPrompt">The prompt that new conversation starts with, when it can go on the command line exactly.</param>
internal sealed record Launch(
    string Directory, string? Conversation = null, string? ResumeOnce = null, string? Theme = null, PanelLink? Panels = null,
    bool NewConversation = false, string? FirstPrompt = null);

/// <summary>
/// How the page tells, from the terminal's screen, that an agent is asking you something (JavaScript regex sources, tried
/// on each line on screen).
/// </summary>
/// <param name="Choice">A line of the menu it asks with, such as a picked numbered choice.</param>
/// <param name="PromptRule">With <paramref name="PromptLine"/>: the rule above its prompt box. While the prompt box is on
/// screen it wants a prompt, so menu-like text above it doesn't count.</param>
/// <param name="PromptLine">The first line of its prompt box, under <paramref name="PromptRule"/>.</param>
internal sealed record AskingScreen(string Choice, string? PromptRule = null, string? PromptLine = null);

/// <summary>The colour the page marks an agent's things with (CSS colours), one for each theme so text stays readable.</summary>
internal sealed record AgentColor(string Dark, string Light);

/// <summary>
/// One coding agent's command-line interface: how the app starts it in a conversation, finds its saved conversations and
/// learns which one it's in, and connects it to panels. Any other command (a shell, a dev server) has no profile and
/// just runs. To support another agent, add a profile to <see cref="Agents.All"/>.
/// </summary>
internal abstract class AgentProfile
{
    /// <summary>The id the page and <see cref="ConversationInfo.Agent"/> use: "claude".</summary>
    public abstract string Id { get; }

    /// <summary>The name shown for it: "Claude".</summary>
    public abstract string Name { get; }

    /// <summary>The program names that start it ("claude", "claude.exe"), compared without case.</summary>
    public abstract IReadOnlyList<string> Programs { get; }

    /// <summary>Its colour, for its tag among the conversations to resume; null leaves the tag plain.</summary>
    public virtual AgentColor? Color => null;

    // ---------------------------------------------------------------- its conversation

    /// <summary>
    /// Whether the app picks a new conversation's id and starts the agent in it (claude --session-id). Otherwise the
    /// agent picks it, and the session learns it from <see cref="LiveConversation"/>.
    /// </summary>
    public abstract bool ChoosesConversationId { get; }

    /// <summary>
    /// Whether the app can look after this command's conversation (the session's <see cref="SessionInfo.Conversation"/>):
    /// the interactive interface, with no options that pick a conversation of their own.
    /// </summary>
    public abstract bool CanTrackConversation(string command);

    /// <summary>Whether starting this command again carries on a conversation it picks itself, rather than starting a new one.</summary>
    public abstract bool CarriesOnConversation(string command);

    /// <summary>
    /// The command without the option that resumes a particular conversation, and that conversation, so the session keeps
    /// it itself: "claude --resume X --model opus" becomes ("claude --model opus", X). Anything else comes back unchanged, with no id.
    /// </summary>
    public abstract (string Command, string? Conversation) TakeConversation(string command);

    /// <summary>The command without an option that carries on the folder's latest conversation (the app picks it instead).</summary>
    public virtual string StripContinue(string command) => command;

    /// <summary>The command line for this start of <paramref name="command"/>.</summary>
    public abstract string CommandLine(string command, Launch launch);

    /// <summary>Anything the agent needs in its environment for this start (e.g. a token it reads from there).</summary>
    public virtual void AddEnvironment(IDictionary<string, string> env, Launch launch) { }

    /// <summary>
    /// The saved conversation the session's agent is in now, or null when that isn't known. <paramref name="others"/> are
    /// the conversations other sessions are in, which this one can't be in.
    /// </summary>
    public abstract string? LiveConversation(TerminalSession session, int[] processIds, IReadOnlySet<string> others);

    /// <summary>Its saved conversations, latest first; only those of <paramref name="directory"/> when one is given.</summary>
    public abstract List<ConversationInfo> ListConversations(string? directory, int max);

    /// <summary>The saved conversation <paramref name="id"/>, or null while the agent hasn't saved anything in it.</summary>
    public abstract ConversationInfo? FindConversation(string id, string directory);

    /// <summary>
    /// Whether the app can read the running agent's conversation as prompts and answers (<see cref="ReadConversation"/>),
    /// for the conversation rail and the Conversation tab.
    /// </summary>
    public virtual bool ShowsConversation => false;

    /// <summary>
    /// The conversation the running session's agent is in, as prompts and answers, or null while there's none to show.
    /// An agent whose transcript is JSONL reads it with <see cref="ConversationReader.Read"/> and a <see cref="TranscriptParser"/>
    /// of its own, which parses only what was added since the last read.
    /// </summary>
    public virtual Conversation? ReadConversation(TerminalSession session, int[] processIds) => null;

    /// <summary>
    /// The session's own conversation read from disk while it isn't running (the one it would resume), for the
    /// assistant to answer about a stopped session; null when there is none or it can't be read.
    /// </summary>
    public virtual Conversation? ReadSavedConversation(TerminalSession session) => null;

    // ---------------------------------------------------------------- panels

    /// <summary>Whether it reads the panel server from a file written for the session (Claude's --mcp-config) rather than its command line.</summary>
    public virtual bool PanelsFromFile => false;

    // ---------------------------------------------------------------- one-off prompts

    /// <summary>
    /// A command that answers one prompt read from standard input with plain text on standard output, changing nothing and
    /// saving no conversation (Suggest writes commit messages with it), or null.
    /// </summary>
    public virtual string? HeadlessCommand => null;

    // ---------------------------------------------------------------- for the page

    /// <summary>How the session follows it to another conversation, in words: "through /clear and /resume".</summary>
    public abstract string Follows { get; }

    /// <summary>A JavaScript regex source for the options with which a command picks its own conversation (the page notes and strips them).</summary>
    public abstract string OwnConversationPattern { get; }

    /// <summary>A JavaScript regex source for the option that carries on the folder's latest conversation, if it has one.</summary>
    public virtual string? ContinuePattern => null;

    /// <summary>
    /// How its screen shows it's asking you something, or null. An agent that reports its state itself (an OSC 7501
    /// sequence with state=blocked, as Codex sends) is taken at its word instead, once it has.
    /// </summary>
    public virtual AskingScreen? Asks => null;

    /// <summary>What the page needs to know about it.</summary>
    public object Describe() => new
    {
        id = Id, name = Name, programs = Programs, color = Color, choosesId = ChoosesConversationId, follows = Follows,
        ownConversation = OwnConversationPattern, @continue = ContinuePattern, showsConversation = ShowsConversation, asks = Asks,
        suggests = HeadlessCommand != null,
    };

    // ---------------------------------------------------------------- shared helpers

    protected static string[] Tokens(string command) => command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The command with <paramref name="argument"/> straight after the program.</summary>
    protected static string AfterProgram(string command, string argument)
    {
        var program = Tokens(command)[0];
        var trimmed = command.Trim();
        return program + " " + argument + trimmed[program.Length..];
    }
}

/// <summary>The agents the app knows.</summary>
internal static class Agents
{
    public static readonly ClaudeAgent Claude = new();
    public static readonly CodexAgent Codex = new();

    public static IReadOnlyList<AgentProfile> All { get; } = [Claude, Codex];

    /// <summary>The agent <paramref name="command"/> starts, from its first word, or null for any other command.</summary>
    public static AgentProfile? For(string? command)
    {
        var program = (command ?? "").Trim().Split(' ', 2)[0];
        return All.FirstOrDefault(a => a.Programs.Contains(program, StringComparer.OrdinalIgnoreCase));
    }
}
