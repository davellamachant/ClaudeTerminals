using ClaudeTerminals.Sessions;

namespace ClaudeTerminals.Tests;

/// <summary>The prompt shown when closing the app with sessions running: it must not suggest they're gone for good.</summary>
public sealed class CloseMessageTests
{
    private static List<TerminalSession> Sessions(params string[] commands) =>
        commands.Select(c => new TerminalSession(new SessionInfo { Name = "s", Directory = ".", Command = c })).ToList();

    [Theory]
    [InlineData("claude --continue", true)]
    [InlineData("claude -c --model opus", true)]
    [InlineData("claude --resume abc", true)]
    [InlineData("claude", false)]
    [InlineData("claude --model opus", false)]
    [InlineData("node fake.mjs --continue", false)]
    public void Continue_and_resume_carry_on_a_conversation(string command, bool expected) =>
        Assert.Equal(expected, TerminalSession.CarriesOnConversation(command));

    [Fact]
    public void Sessions_that_continue_are_said_to_pick_up_where_they_left_off()
    {
        var m = MainForm.CloseMessage(Sessions("claude --continue", "claude --resume x"));
        Assert.StartsWith("2 sessions are running. Closing stops them, but nothing is lost", m);
        Assert.Contains("pick up where they left off", m);
        Assert.DoesNotContain("terminated", m);
        Assert.DoesNotContain("new conversation", m);
    }

    [Fact]
    public void A_plain_claude_session_is_said_to_start_a_new_conversation_with_the_old_one_reopenable()
    {
        var m = MainForm.CloseMessage(Sessions("claude"));
        Assert.StartsWith("1 session is running. Closing stops it", m);
        Assert.Contains("with a new conversation", m);
        Assert.Contains("Resume a different conversation", m);
    }

    [Fact]
    public void A_mix_says_how_many_of_each()
    {
        var m = MainForm.CloseMessage(Sessions("claude --continue", "claude --continue", "claude"));
        Assert.Contains("2 pick up where they left off, 1 starts a new conversation", m);
    }
}
