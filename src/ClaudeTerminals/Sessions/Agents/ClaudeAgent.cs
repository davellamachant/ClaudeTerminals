using System.Text.RegularExpressions;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Sessions;

/// <summary>
/// Claude Code. The app gives a new session's conversation its id (--session-id), reopens it with --resume, and follows
/// Claude to another one (/clear, /resume) through ~/.claude/sessions/&lt;pid&gt;.json (see <see cref="ConversationReader"/>).
/// Its saved conversations are in ~/.claude/projects (see <see cref="ConversationIndex"/>).
/// </summary>
internal sealed partial class ClaudeAgent : AgentProfile
{
    public override string Id => "claude";
    public override string Name => "Claude";
    public override IReadOnlyList<string> Programs { get; } = ["claude", "claude.exe"];
    public override AgentColor Color { get; } = new("#d97757", "#c15f3c");   // Claude's coral
    public override bool ChoosesConversationId => true;
    public override bool PanelsFromFile => true;
    public override string Follows => "through /clear and /resume";
    public override string OwnConversationPattern => @"\s+(?:-r|--resume|--session-id|--fork-session)(?:=\S+|\s+[^-\s]\S*)?(?=\s|$)";
    public override string? ContinuePattern => @"\s+(?:-c|--continue)(?=\s|$)";

    /// <summary>Haiku with no tools or MCP servers, not saved (so it doesn't appear among the conversations to resume).</summary>
    public override string HeadlessCommand => "claude -p --model haiku --tools \"\" --strict-mcp-config --no-session-persistence --output-format text";

    /// <summary>
    /// Claude asks by putting a menu where its prompt box was: a permission prompt, a question (AskUserQuestion), a plan
    /// to approve, the folder trust check. Each has a picked numbered choice ("❯ 1. Yes") or an "Esc to cancel" hint.
    /// </summary>
    public override AskingScreen Asks { get; } = new(@"^\s*[>❯›] ?\d+\. |\bEsc to cancel\b", @"^\s*─{8,}", @"^[>!❯](\s|$)");

    /// <summary>Options with which a command picks its own conversation; the app then leaves the choice to it.</summary>
    private static readonly string[] OwnConversationFlags = ["-r", "--resume", "--session-id", "--fork-session"];

    public override bool CanTrackConversation(string command)
    {
        var flags = Tokens(command).Skip(1).Select(t => t.Split('=')[0]).ToList();
        return !flags.Any(f => OwnConversationFlags.Concat(["-p", "--print"]).Contains(f, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>--continue or --resume.</summary>
    public override bool CarriesOnConversation(string command)
    {
        string[] carryOn = ["-c", "--continue", "-r", "--resume"];
        return Tokens(command).Skip(1).Any(t => carryOn.Contains(t.Split('=')[0], StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>"--resume &lt;id&gt;", -r, or --resume=&lt;id&gt;.</summary>
    public override (string Command, string? Conversation) TakeConversation(string command)
    {
        var m = ResumeFlag().Match(command);
        if (!m.Success || !TerminalSession.IsConversationId(m.Groups[1].Value)) return (command, null);
        var rest = (command[..m.Index] + command[(m.Index + m.Length)..]).Trim();
        return CanTrackConversation(rest) ? (rest, m.Groups[1].Value) : (command, null);
    }

    [GeneratedRegex(@"\s+(?:--resume|-r)(?:=|\s+)([0-9A-Fa-f-]{36})(?=\s|$)")]
    private static partial Regex ResumeFlag();

    /// <summary>Without --continue / -c.</summary>
    public override string StripContinue(string command) => WithoutContinue(command);

    public override string CommandLine(string command, Launch launch) =>
        PrepareCommand(command, launch.ResumeOnce, () => ConversationIndex.HasConversation(launch.Directory), ThemeSettingsFile(launch.Theme),
            launch.Panels?.ConfigFile, launch.NewConversation, launch.FirstPrompt, launch.Conversation,
            id => ConversationReader.IsSaved(id, launch.Directory));

    /// <summary>
    /// The command line for this start of a claude session. A command that names its own conversation (--resume,
    /// --session-id, --fork-session) is left alone; otherwise:
    /// - with the session's own <paramref name="conversation"/>: --resume it once Claude has saved it
    ///   (<paramref name="isSaved"/>), or --session-id it before then (resuming an unsaved one fails with "No conversation
    ///   found"), so Claude starts that very conversation; any --continue is dropped;
    /// - after an update, the conversation it was in (<paramref name="resumeId"/>) replaces --continue, or is added to a
    ///   plain claude;
    /// - otherwise --continue is dropped when the folder has no saved conversation yet (<paramref name="hasConversation"/>),
    ///   since claude would stop with "No conversation found to continue". The saved command keeps it for next time.
    /// Anything that isn't an interactive claude is left alone.
    /// </summary>
    /// <param name="themeSettings">
    /// A settings file naming Claude's theme to match the app's terminals (Claude can't ask the terminal itself: the
    /// console layer swallows the question). Added unless the command already passes its own --settings.
    /// </param>
    /// <param name="mcpConfig">
    /// The file connecting Claude to the app's panel server, whose tools are then allowed without asking. Added unless the
    /// command passes its own --mcp-config.
    /// </param>
    /// <param name="newConversation">Start a new conversation (a session started from a panel): --continue is dropped.</param>
    /// <param name="firstPrompt">
    /// The prompt that conversation starts with, put straight after the program: the option lists added after it
    /// (--mcp-config, --allowedTools) would otherwise take it as one of theirs.
    /// </param>
    internal static string PrepareCommand(string command, string? resumeId, Func<bool> hasConversation, string? themeSettings = null,
        string? mcpConfig = null, bool newConversation = false, string? firstPrompt = null,
        string? conversation = null, Func<string, bool>? isSaved = null)
    {
        if (Agents.For(command) is not ClaudeAgent) return command;
        var flags = Tokens(command).Skip(1).Select(t => t.Split('=')[0]).ToList();
        bool Has(params string[] names) => flags.Any(f => names.Contains(f, StringComparer.OrdinalIgnoreCase));
        if (Has("-p", "--print")) return command;   // no interface, so nothing to theme or continue

        var result = command.Trim();
        if (!Has(OwnConversationFlags))
        {
            if (TerminalSession.IsConversationId(conversation))
                result = WithoutContinue(command) + (isSaved?.Invoke(conversation!) == true ? " --resume " : " --session-id ") + conversation;
            else if (newConversation)
                result = WithoutContinue(command);
            else if (resumeId != null && TerminalSession.IsPlainId(resumeId))
                result = WithoutContinue(command) + " --resume " + resumeId;
            else if (Has("-c", "--continue") && !hasConversation())
                result = WithoutContinue(command);
        }
        if (firstPrompt != null)
            result = AfterProgram(result, CommandLineResolver.QuoteArgument(firstPrompt));
        if (themeSettings != null && !Has("--settings"))
            result = result.TrimEnd() + " --settings \"" + themeSettings + "\"";
        if (mcpConfig != null && !Has("--mcp-config"))
            result = result.TrimEnd() + " --mcp-config \"" + mcpConfig + "\" --allowedTools " + "mcp__" + Panels.PanelMcp.ServerName;
        return result;
    }

    /// <summary>The settings file that sets Claude's theme to "light" or "dark", written once into the app's data folder.</summary>
    internal static string? ThemeSettingsFile(string? theme)
    {
        if (theme is not ("light" or "dark")) return null;
        try
        {
            var path = Path.Combine(SessionStore.AppDataFolder, $"claude-theme-{theme}.json");
            var content = $$"""{"theme":"{{theme}}"}""";
            if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // Claude just uses its own theme setting
        }
    }

    /// <summary>The one Claude names for its process; only one Claude has saved counts (an unsaved one can't be resumed).</summary>
    public override string? LiveConversation(TerminalSession session, int[] processIds, IReadOnlySet<string> others) =>
        ConversationReader.ResumableConversation(processIds);

    public override List<ConversationInfo> ListConversations(string? directory, int max) => ConversationIndex.List(directory, max);

    public override ConversationInfo? FindConversation(string id, string directory) => ConversationIndex.Find(id, directory);

    public override bool ShowsConversation => true;

    /// <summary>The one Claude names for its process (it can be in another than the session's own until it has saved it).</summary>
    public override Conversation? ReadConversation(TerminalSession session, int[] processIds) => ConversationReader.ForProcesses(processIds);

    private static string WithoutContinue(string command) => ContinueFlag().Replace(command.Trim(), "");

    [GeneratedRegex(@"\s+(?:-c|--continue)(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ContinueFlag();
}
