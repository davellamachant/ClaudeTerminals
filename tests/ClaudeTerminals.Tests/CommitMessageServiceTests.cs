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
        Assert.Equal((program, arguments), CommitMessageService.SplitProgram(line));
}
