using ClaudeTerminals.Git;

namespace ClaudeTerminals.Tests;

/// <summary>The suggested commit message: what Claude is asked, and how its answer is tidied. Claude itself isn't run.</summary>
public sealed class CommitMessageServiceTests : IDisposable
{
    private readonly TempRepo repo = new();

    public void Dispose() => repo.Dispose();

    [Fact]
    public async Task With_nothing_staged_it_describes_every_change_including_new_files()
    {
        repo.Commit(("calc.py", "def total(items):\n    return sum(items)\n"));
        repo.Git("commit", "--allow-empty", "-m", "feat: earlier work");
        repo.Write("calc.py", "def total(items, tax=0):\n    return sum(items) * (1 + tax)\n");
        repo.Write("notes/new.md", "# Brand new file\n");

        var prompt = await CommitMessageService.BuildPromptAsync(repo.Root);

        Assert.NotNull(prompt);
        Assert.Contains("feat: earlier work", prompt);                 // recent subjects, for style
        Assert.Contains("M calc.py", prompt);
        Assert.Contains("A notes/new.md", prompt);
        Assert.Contains("+def total(items, tax=0):", prompt);
        Assert.Contains("--- new file: notes/new.md", prompt);
        Assert.Contains("# Brand new file", prompt);
    }

    [Fact]
    public async Task With_something_staged_it_describes_only_the_staged_changes()
    {
        repo.Commit(("a.txt", "one\n"), ("b.txt", "two\n"));
        repo.Write("a.txt", "one changed\n");
        repo.Write("b.txt", "two changed\n");
        repo.Git("add", "a.txt");

        var prompt = await CommitMessageService.BuildPromptAsync(repo.Root);

        Assert.NotNull(prompt);
        Assert.Contains("+one changed", prompt);
        Assert.DoesNotContain("two changed", prompt);
        Assert.DoesNotContain("b.txt", prompt);
    }

    [Theory]
    [InlineData("short", "that one line only")]
    [InlineData("standard", "Usually that line is enough")]
    [InlineData("detailed", "Then a blank line and a body")]
    [InlineData(null, "Usually that line is enough")]       // the default
    [InlineData("chatty", "Usually that line is enough")]   // unknown levels fall back to the default
    public async Task Each_level_asks_for_its_own_length(string? level, string rule)
    {
        repo.Commit(("a.txt", "one\n"));
        repo.Write("a.txt", "two\n");
        var prompt = await CommitMessageService.BuildPromptAsync(repo.Root, CommitMessageService.NormalizeDetail(level));
        Assert.Contains(rule, prompt);
    }

    [Fact]
    public void A_short_message_is_only_its_summary_line_whatever_comes_back()
    {
        const string answer = "Add tax rounding per line item\n\nTax was rounded once per cart.\n- more";
        Assert.Equal("Add tax rounding per line item", CommitMessageService.Finish(answer, "short"));
        Assert.Equal("Add tax rounding per line item\n\nTax was rounded once per cart.\n- more", CommitMessageService.Finish(answer, "standard"));
    }

    [Fact]
    public async Task A_clean_repository_has_nothing_to_describe()
    {
        repo.Commit(("a.txt", "one\n"));
        Assert.Null(await CommitMessageService.BuildPromptAsync(repo.Root));
    }

    [Theory]
    [InlineData("```\nFix the thing\n```", "Fix the thing")]
    [InlineData("```text\nFix the thing\n\nBody.\n```", "Fix the thing\n\nBody.")]
    [InlineData("\"Fix the thing\"", "Fix the thing")]
    [InlineData("  Fix the thing\r\n\r\nBody.  ", "Fix the thing\n\nBody.")]
    public void Fences_quotes_and_stray_whitespace_are_removed(string answer, string expected) =>
        Assert.Equal(expected, CommitMessageService.Clean(answer));

    [Fact]
    public void Body_paragraphs_and_list_items_are_wrapped_at_72_but_the_summary_is_not()
    {
        var summary = "Add a tax parameter to total() so callers can include sales tax in the result";
        var body = string.Join(" ", Enumerable.Repeat("word", 30));
        var item = "- " + string.Join(" ", Enumerable.Repeat("item", 25));

        var lines = CommitMessageService.WrapBody($"{summary}\n\n{body}\n{item}").Split('\n');

        Assert.Equal(summary, lines[0]);
        Assert.Equal("", lines[1]);
        Assert.All(lines.Skip(1), l => Assert.True(l.Length <= 72, l));
        Assert.Equal(string.Join(" ", Enumerable.Repeat("word", 30)),
            string.Join(" ", lines.Skip(2).TakeWhile(l => !l.StartsWith('-') && !l.StartsWith("  ")).Select(l => l.Trim())));
        var itemLines = lines.SkipWhile(l => !l.StartsWith("- ")).ToList();
        Assert.True(itemLines.Count > 1);
        Assert.All(itemLines.Skip(1), l => Assert.StartsWith("  item", l));   // hanging indent under the text
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\claude\\claude.exe\" -p --x", "C:\\Program Files\\claude\\claude.exe", "-p --x")]
    [InlineData("C:\\tools\\claude.exe -p", "C:\\tools\\claude.exe", "-p")]
    [InlineData("C:\\Windows\\system32\\cmd.exe /d /s /c \"\"C:\\npm\\claude.cmd\" -p\"", "C:\\Windows\\system32\\cmd.exe", "/d /s /c \"\"C:\\npm\\claude.cmd\" -p\"")]
    public void The_resolved_command_line_splits_into_program_and_arguments(string line, string program, string arguments) =>
        Assert.Equal((program, arguments), Sessions.HeadlessAgent.SplitProgram(line));

    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("claude --model opus", "claude")]
    [InlineData("pwsh", "claude")]   // a session with no agent
    public void The_sessions_own_agent_writes_the_message(string command, string writer) =>
        Assert.Equal(writer, CommitMessageService.Writer(Sessions.Agents.For(command)).Id);

    [Fact]
    public void Each_agent_writes_it_without_saving_a_conversation_or_changing_files()
    {
        Assert.Contains("--no-session-persistence", Sessions.Agents.Claude.HeadlessCommand);
        Assert.Contains("--ephemeral", Sessions.Agents.Codex.HeadlessCommand);
        Assert.Contains("-s read-only", Sessions.Agents.Codex.HeadlessCommand);
        Assert.EndsWith(" -", Sessions.Agents.Codex.HeadlessCommand);   // the prompt comes on standard input
    }
}
