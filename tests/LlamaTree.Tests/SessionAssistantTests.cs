using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>What the assistant asks the agent about your sessions, and which agent answers. The agent itself isn't run.</summary>
public sealed class SessionAssistantTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 10, 0, 0, DateTimeKind.Local);

    private static ConversationTurn Turn(int index, string prompt, DateTime time, params (string Kind, string Text, string? Tool)[] items)
    {
        var t = new ConversationTurn { Index = index, Prompt = prompt, Time = time };
        foreach (var (kind, text, tool) in items) t.Items.Add(new ConversationItem(kind, text, tool));
        return t;
    }

    private static Conversation Conv(params ConversationTurn[] turns) => new("c1", "c1.jsonl", turns.Length, turns);

    private static SessionBrief Brief(string name, Conversation? conversation, string status = "running", string? project = "Shop", AgentProfile? agent = null) =>
        new(name.ToLowerInvariant(), name, project, @"C:\work\" + name, status, agent ?? Agents.Claude, conversation);

    private static string Prompt(string question, IReadOnlyList<SessionBrief> sessions, IReadOnlyList<AssistantExchange>? history = null) =>
        SessionAssistant.BuildPrompt(question, history ?? [], sessions, Now).Replace("\r\n", "\n");

    [Fact]
    public void The_prompt_lists_the_sessions_their_conversations_and_the_question_last()
    {
        var main = Brief("Checkout", Conv(Turn(0, "Add a tax column", Now.AddMinutes(-40),
            ("tool", "Read: calc.py", "Read"), ("text", "Added the column.\nShould I delete the old migration?", null))), "attention");
        var roles = Brief("Billing", null, "exited", project: null);

        var prompt = Prompt("what is Checkout waiting on?", [main, roles]);

        Assert.Contains("Now: 2026-10-31 10:00", prompt);
        Assert.Contains("Sessions (2):\n- Checkout: Claude, project Shop, folder C:\\work\\Checkout; finished its last turn and is waiting for the person; 1 turn.\n" +
                        "- Billing: Claude, folder C:\\work\\Billing; not running; no conversation to read.\n", prompt);
        Assert.Contains("=== Session \"Checkout\" ===\n--- turn 1 (2026-10-31 09:20)\nPerson: Add a tax column\n[tool Read] Read: calc.py\nAgent: Added the column.\nShould I delete the old migration?\n", prompt);
        Assert.Contains("=== Session \"Billing\" ===\n(no conversation to read)\n", prompt);
        Assert.EndsWith("Question:\nwhat is Checkout waiting on?\n", prompt);
        Assert.DoesNotContain("Earlier in this chat", prompt);
    }

    [Fact]
    public void A_long_conversation_keeps_its_newest_turns_in_order_and_says_what_was_left_out()
    {
        var turns = Enumerable.Range(0, 30).Select(i => Turn(i, $"prompt {i} " + new string('x', 900), Now.AddMinutes(i - 60), ("text", $"answer {i}", null))).ToArray();

        var text = SessionAssistant.Transcript(Conv(turns), 4000).Replace("\r\n", "\n");

        Assert.StartsWith("(26 older turns left out)\n--- turn 27 ", text);
        Assert.Contains("answer 26\n", text);
        Assert.True(text.IndexOf("--- turn 29 ") < text.IndexOf("--- turn 30 "), "oldest kept turn first");
        Assert.DoesNotContain("prompt 25 ", text);
        Assert.True(text.Length < 4200, $"{text.Length} chars");
    }

    [Fact]
    public void A_turn_too_long_for_the_room_is_cut_rather_than_dropped()
    {
        var text = SessionAssistant.Transcript(Conv(Turn(0, "p", Now, ("text", new string('y', 3000), null))), 500).Replace("\r\n", "\n");
        Assert.StartsWith("--- turn 1 ", text);
        Assert.EndsWith("…\n", text);
        Assert.True(text.Length < 520);
    }

    [Fact]
    public void Prompts_and_answers_are_clipped_and_tool_lines_kept_to_one_line()
    {
        var turn = Turn(0, new string('p', 5000), Now, ("tool", "Bash: run\n  the tests", "Bash"), ("text", new string('a', 5000), null));
        var text = SessionAssistant.Transcript(Conv(turn), 100_000).Replace("\r\n", "\n");
        Assert.Contains("Person: " + new string('p', SessionAssistant.MaxPromptChars) + "…\n", text);
        Assert.Contains("[tool Bash] Bash: run the tests\n", text);
        Assert.Contains("Agent: " + new string('a', SessionAssistant.MaxTextChars) + "…\n", text);
    }

    [Fact]
    public void Earlier_questions_go_in_oldest_first_the_last_eight_only()
    {
        var history = Enumerable.Range(1, 10).Select(i => new AssistantExchange($"question {i}", $"answer {i}")).ToList();
        var prompt = Prompt("and Billing?", [Brief("Checkout", null)], history);
        Assert.Contains("Earlier in this chat with the assistant (oldest first):\nQ: question 3\nA: answer 3\n", prompt);
        Assert.Contains("Q: question 10\nA: answer 10\n\nQuestion:\nand Billing?\n", prompt);
        Assert.DoesNotContain("question 2", prompt);
    }

    [Theory]
    [InlineData("codex", "codex", "codex")]      // both Codex: Codex answers
    [InlineData("claude", "codex", "claude")]    // mixed: Claude
    [InlineData("pwsh", "pwsh", "claude")]       // no agent: Claude
    [InlineData("claude", "pwsh", "claude")]
    public void The_sessions_own_agent_answers_when_they_share_one(string a, string b, string writer) =>
        Assert.Equal(writer, SessionAssistant.WriterFor([Brief("A", null, agent: Agents.For(a)), Brief("B", null, agent: Agents.For(b))]).Id);

    [Fact]
    public void Each_sessions_agent_is_named_as_it_is_even_a_shell()
    {
        var prompt = Prompt("q", [Brief("Shell", null, agent: null) with { Agent = null }, Brief("Cx", null, agent: Agents.Codex, status: "working")]);
        Assert.Contains("- Shell: a shell, project Shop, folder C:\\work\\Shell; idle, waiting for a prompt; no conversation to read.", prompt);
        Assert.Contains("- Cx: Codex, project Shop, folder C:\\work\\Cx; working now; no conversation to read.", prompt);
    }

    [Theory]
    [InlineData("```\nCheckout is waiting on you.\n```", "Checkout is waiting on you.")]
    [InlineData("  Just text.  \r\n", "Just text.")]
    [InlineData("```md\n- one\n- two\n```\n", "- one\n- two")]
    public void An_answer_wrapped_in_a_code_fence_is_unwrapped(string raw, string clean) =>
        Assert.Equal(clean, SessionAssistant.Clean(raw));

    [Fact]
    public void Older_turns_that_speak_of_what_the_question_asks_about_are_kept_with_the_newest()
    {
        // Forty turns of other work, with the themes made at turns 6 and 13: the question brings those back, in order, with the gaps said.
        var turns = Enumerable.Range(0, 40).Select(i => Turn(i, $"prompt {i} " + new string('x', 700), Now.AddMinutes(i - 60), ("text", $"answer {i} " + new string('y', 200), null))).ToArray();
        turns[5] = Turn(5, "Make a Cyberpunk 2077 theme like the other themes", Now.AddMinutes(-55),
            ("tool", "Write: cyberpunk-2077.json", "Write"), ("text", "The theme file sets every colour; a glitching screen is drawn on the floor.", null));
        turns[12] = Turn(12, "Something else " + new string('x', 700), Now.AddMinutes(-48), ("text", "Done. The themes' tests pass, and the theme's README paragraph is in.", null));

        var text = SessionAssistant.Transcript(Conv(turns), 6000, "How were the themes made?").Replace("\r\n", "\n");

        Assert.StartsWith("(5 older turns left out)\n--- turn 6 ", text);
        Assert.Contains("Person: Make a Cyberpunk 2077 theme like the other themes\n[tool Write] Write: cyberpunk-2077.json\nAgent: The theme file sets every colour", text);
        Assert.Contains("(6 turns left out)\n--- turn 13 ", text);
        Assert.Contains("answer 39 ", text);
        Assert.True(text.IndexOf("--- turn 13 ") < text.IndexOf("--- turn 38 "), "oldest kept turn first");
        Assert.DoesNotContain("prompt 20 ", text);
        Assert.True(text.Length <= 6100, $"{text.Length} chars");
    }

    [Fact]
    public void Without_a_question_or_with_nothing_to_match_only_the_newest_turns_are_kept()
    {
        var turns = Enumerable.Range(0, 10).Select(i => Turn(i, $"prompt {i} " + new string('x', 900), Now.AddMinutes(i - 60), ("text", i == 2 ? "the themes" : $"answer {i}", null))).ToArray();
        var plain = SessionAssistant.Transcript(Conv(turns), 2500).Replace("\r\n", "\n");
        var small = SessionAssistant.Transcript(Conv(turns), 2500, "what is it doing?").Replace("\r\n", "\n");   // only small words: nothing to match
        Assert.StartsWith("(8 older turns left out)\n--- turn 9 ", plain);
        Assert.Equal(plain, small);
    }

    [Fact]
    public void A_relevant_turn_too_big_for_the_room_left_is_passed_over_for_a_smaller_one()
    {
        var turns = new[]
        {
            Turn(0, "themes: the big one " + new string('x', 3000), Now.AddMinutes(-60), ("text", "themes themes themes", null)),
            Turn(1, "themes: the small one", Now.AddMinutes(-50), ("text", "a theme", null)),
            Turn(2, "newest " + new string('x', 400), Now.AddMinutes(-40), ("text", "answer", null)),
        };
        var text = SessionAssistant.Transcript(Conv(turns), 1600, "how were the themes made?").Replace("\r\n", "\n");
        Assert.Contains("--- turn 2 ", text);
        Assert.Contains("--- turn 3 ", text);
        Assert.StartsWith("(1 older turn left out)\n", text);
    }

    [Theory]
    [InlineData("How were the themes made?", "them")]   // the stem, the same on both sides
    [InlineData("which session is glitching the floor and the glitches", "glitch floor")]
    [InlineData("what did you decide about the accent colours?", "decid accent colour")]
    [InlineData("what is it doing?", "")]
    public void A_questions_words_are_the_ones_worth_matching_as_stems(string question, string expected) =>
        Assert.Equal(expected, string.Join(' ', SessionAssistant.QuestionWords(question)));

    [Fact]
    public void A_turn_scores_by_the_questions_words_in_its_prompt_and_answer()
    {
        var words = SessionAssistant.QuestionWords("how were the themes made?");
        Assert.Equal(0, SessionAssistant.Relevance(Turn(0, "fix the tests", Now, ("text", "done", null)), words));
        Assert.Equal(1, SessionAssistant.Relevance(Turn(0, "fix the tests", Now, ("text", "the theme loads", null)), words));
        Assert.Equal(2, SessionAssistant.Relevance(Turn(0, "fix the tests", Now, ("text", "theme, themes, themed", null)), words));
        Assert.Equal(3, SessionAssistant.Relevance(Turn(0, "make a theme", Now, ("text", "the theme loads", null)), words));
    }

    [Fact]
    public async Task Asking_nothing_or_about_no_sessions_fails_before_any_agent_runs()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SessionAssistant.AskAsync(" ", [], [Brief("Checkout", null)], Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SessionAssistant.AskAsync("hello?", [], [], Now));
    }
}
