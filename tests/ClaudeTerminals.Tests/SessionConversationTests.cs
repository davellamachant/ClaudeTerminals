using ClaudeTerminals.Sessions;

namespace ClaudeTerminals.Tests;

/// <summary>Each session's own conversation: chosen by the app, started with --session-id, reopened with --resume.</summary>
public class SessionConversationTests
{
    private const string Id = "3f2a6c1e-8b4d-4c2a-9e1f-0a1b2c3d4e5f";

    [Theory]
    [InlineData("claude", true, "claude --resume " + Id)]
    [InlineData("claude", false, "claude --session-id " + Id)]                 // nothing saved yet: start that very conversation
    [InlineData("claude --continue", true, "claude --resume " + Id)]           // its own conversation, not the folder's latest
    [InlineData("claude -c --model opus", false, "claude --model opus --session-id " + Id)]
    [InlineData("claude --resume other", true, "claude --resume other")]       // the command picks its own
    [InlineData("claude --session-id abc", true, "claude --session-id abc")]
    [InlineData("claude -p hello", true, "claude -p hello")]
    [InlineData("pwsh", true, "pwsh")]
    public void A_session_reopens_its_own_conversation(string command, bool saved, string expected) =>
        Assert.Equal(expected, ClaudeAgent.PrepareCommand(command, null, () => true, conversation: Id, isSaved: _ => saved));

    [Fact]
    public void Its_own_conversation_wins_over_an_update_note_and_a_folders_latest()
    {
        var other = "11111111-2222-3333-4444-555555555555";
        Assert.Equal("claude --resume " + Id, ClaudeAgent.PrepareCommand("claude --continue", other, () => true, conversation: Id, isSaved: _ => true));
    }

    [Fact]
    public void A_session_started_from_a_panel_starts_its_conversation_with_the_prompt() =>
        Assert.Equal($"claude \"Fix it\" --session-id {Id} --mcp-config \"M.json\" --allowedTools mcp__claude-terminals",
            ClaudeAgent.PrepareCommand("claude", null, () => true, mcpConfig: "M.json", newConversation: true, firstPrompt: "Fix it", conversation: Id, isSaved: _ => false));

    [Fact]
    public void Only_a_real_session_id_is_used()
    {
        foreach (var bad in new[] { "abc", "not-a-guid-at-all", "3f2a6c1e-8b4d-4c2a-9e1f-0a1b2c3d4e5f; calc" })
            Assert.Equal("claude", ClaudeAgent.PrepareCommand("claude", null, () => true, conversation: bad, isSaved: _ => true));
        Assert.True(TerminalSession.IsConversationId(Id));
    }

    [Theory]
    [InlineData("claude --resume " + Id, "claude", Id)]
    [InlineData("claude --resume " + Id + " --model opus", "claude --model opus", Id)]
    [InlineData("claude --model opus -r " + Id, "claude --model opus", Id)]
    [InlineData("claude --resume=" + Id, "claude", Id)]
    [InlineData("claude --resume abc", "claude --resume abc", null)]           // not a session id: left as typed
    [InlineData("claude --continue", "claude --continue", null)]
    [InlineData("pwsh --resume " + Id, "pwsh --resume " + Id, null)]
    public void A_resume_in_the_command_becomes_the_sessions_conversation(string command, string rest, string? id) =>
        Assert.Equal((rest, id), TerminalSession.TakeConversation(command));

    [Theory]
    [InlineData("claude", true)]
    [InlineData("claude --continue --model opus", true)]
    [InlineData("claude --resume x", false)]
    [InlineData("claude --session-id x", false)]
    [InlineData("claude --fork-session", false)]
    [InlineData("claude -p hi", false)]
    [InlineData("cmd /k", false)]
    public void The_app_looks_after_the_conversation_of_an_interactive_claude_that_doesnt_pick_its_own(string command, bool expected) =>
        Assert.Equal(expected, TerminalSession.CanTrackConversation(command));

    [Theory]
    [InlineData("claude", null, true)]
    [InlineData("claude --model opus", null, true)]
    [InlineData("claude", Id, false)]
    [InlineData("claude --continue", null, false)]                             // learns the folder's latest once Claude has picked it
    [InlineData("claude --resume " + Id, null, false)]                         // the command picks its own
    [InlineData("claude -p hi", null, false)]
    [InlineData("pwsh", null, false)]
    public void A_claude_session_without_a_conversation_gets_one_of_its_own(string command, string? conversation, bool expected) =>
        Assert.Equal(expected, TerminalSession.NeedsOwnConversation(new SessionInfo { Command = command, Conversation = conversation }));

    [Fact]
    public void A_session_saved_before_it_had_its_own_conversation_gets_a_new_one_and_keeps_it()
    {
        using var s = new TerminalSession(new SessionInfo { Name = "s", Directory = ".", Command = "claude" });
        Assert.True(MainForm.GiveOwnConversation(s));   // new: the one it was in isn't known
        Assert.True(TerminalSession.IsConversationId(s.Info.Conversation));
        Assert.False(TerminalSession.NeedsOwnConversation(s.Info));
        Assert.Equal("claude --session-id " + s.Info.Conversation,   // not a plain claude, so the next start reopens it
            ClaudeAgent.PrepareCommand(s.Info.Command, null, () => true, conversation: s.Info.Conversation, isSaved: _ => false));
    }

    [Fact]
    public void A_session_an_update_noted_keeps_the_conversation_it_was_in()
    {
        using var s = new TerminalSession(new SessionInfo { Name = "s", Directory = ".", Command = "claude" }) { ResumeOnce = Id };
        Assert.False(MainForm.GiveOwnConversation(s));
        Assert.Equal(Id, s.Info.Conversation);
    }

    [Fact]
    public void A_session_with_its_own_conversation_carries_on_when_it_restarts()
    {
        Assert.True(TerminalSession.CarriesOnConversation(new SessionInfo { Command = "claude", Conversation = Id }));
        Assert.False(TerminalSession.CarriesOnConversation(new SessionInfo { Command = "claude" }));
        Assert.False(TerminalSession.CarriesOnConversation(new SessionInfo { Command = "pwsh", Conversation = Id }));
    }
}
