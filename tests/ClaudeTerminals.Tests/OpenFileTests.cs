using ClaudeTerminals.Git;

namespace ClaudeTerminals.Tests;

/// <summary>"Open" / "Open file" (review finding F2): never run a file, never leave the repository.</summary>
public sealed class OpenFileTests
{
    private const string Root = @"C:\work\repo";

    [Theory]
    [InlineData("a.txt", @"C:\work\repo\a.txt")]
    [InlineData("sub/b.cs", @"C:\work\repo\sub\b.cs")]
    [InlineData(@"sub\b.cs", @"C:\work\repo\sub\b.cs")]
    [InlineData("sub/../c.md", @"C:\work\repo\c.md")]
    [InlineData("spa ce & ünï.txt", @"C:\work\repo\spa ce & ünï.txt")]
    public void Paths_inside_the_repository_resolve(string relative, string expected) =>
        Assert.Equal(expected, GitService.FullPathInRepo(Root, relative));

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData(@"..\repo2\sibling.txt")]   // sibling folder whose name starts with the repository's
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"\\server\share\x.txt")]
    [InlineData("/x.txt")]
    [InlineData("")]
    public void Paths_outside_the_repository_are_rejected(string relative) =>
        Assert.Null(GitService.FullPathInRepo(Root, relative));

    [Theory]
    [InlineData("run.cmd")]
    [InlineData("RUN.BAT")]
    [InlineData("tool.Js")]
    [InlineData("x.vbs")]
    [InlineData("deploy.ps1")]
    [InlineData("script.py")]
    [InlineData("app.exe")]
    [InlineData("old.com")]
    [InlineData("page.hta")]   // not in the requirement's list: caught by Windows' own unsafe-type list
    [InlineData("setup.msi")]
    [InlineData("saver.scr")]
    public void Scripts_and_executables_count_as_runnable(string file) =>
        Assert.True(MainForm.IsRunnable(file));

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("README.md")]
    [InlineData("notes.txt")]
    [InlineData("package.json")]
    [InlineData("icon.png")]
    [InlineData("Makefile")]
    public void Documents_are_not_runnable(string file) =>
        Assert.False(MainForm.IsRunnable(file));

    // An extension on none of the lists, associated on some machine with a program that runs it.
    [Theory]
    [InlineData("\"%1\" %*", @"C:\tools\thing.exe", true)]               // runs the file itself, like .exe
    [InlineData("%1 %*", null, true)]
    [InlineData("\"C:\\Python313\\python.exe\" \"%1\" %*", @"C:\Python313\python.exe", true)]
    [InlineData("\"C:\\Python313\\python3.13.exe\" \"%1\"", @"C:\Python313\python3.13.exe", true)]
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" \"%1\"", @"C:\Program Files\nodejs\node.exe", true)]
    [InlineData("\"C:\\Program Files\\AutoHotkey\\AutoHotkey64.exe\" \"%1\" %*", @"C:\Program Files\AutoHotkey\AutoHotkey64.exe", true)]
    [InlineData("%SystemRoot%\\system32\\NOTEPAD.EXE %1", @"C:\Windows\system32\NOTEPAD.EXE", false)]
    [InlineData("\"C:\\Program Files\\Microsoft VS Code\\Code.exe\" \"%1\"", @"C:\Program Files\Microsoft VS Code\Code.exe", false)]
    [InlineData(null, null, false)]
    public void Default_actions_that_run_the_file_are_detected(string? command, string? program, bool runs) =>
        Assert.Equal(runs, MainForm.DefaultActionRuns(command, program));

    [Fact]
    public void This_machines_cmd_association_counts_as_running() =>
        Assert.True(MainForm.DefaultActionRuns(
            ClaudeTerminals.Terminal.Native.AssocQuery(ClaudeTerminals.Terminal.Native.ASSOCSTR_COMMAND, ".cmd"),
            ClaudeTerminals.Terminal.Native.AssocQuery(ClaudeTerminals.Terminal.Native.ASSOCSTR_EXECUTABLE, ".cmd")));

    [Fact]
    public void Files_reached_through_a_junction_out_of_the_repository_are_rejected()
    {
        var top = Path.Combine(Path.GetTempPath(), "ct-junction-" + Guid.NewGuid().ToString("N")[..8]);
        var repo = Path.Combine(top, "repo");
        var outside = Path.Combine(top, "outside");
        var alias = Path.Combine(top, "alias");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(repo, "inside.txt"), "in");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "out");
        try
        {
            Junction(Path.Combine(repo, "link"), outside);
            Junction(alias, repo);

            Assert.NotNull(GitService.FullPathInRepo(repo, @"link\secret.txt"));        // lexically inside...
            Assert.Null(GitService.ExistingPathInRepo(repo, @"link\secret.txt"));        // ...but really outside
            Assert.NotNull(GitService.ExistingPathInRepo(repo, "inside.txt"));
            Assert.NotNull(GitService.ExistingPathInRepo(alias, "inside.txt"));          // repository opened through a link
            Assert.Null(GitService.ExistingPathInRepo(repo, "missing.txt"));
        }
        finally
        {
            Directory.Delete(Path.Combine(repo, "link"));   // removes the junction, not its target
            Directory.Delete(alias);
            Directory.Delete(top, true);
        }
    }

    private static void Junction(string link, string target)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "could not create junction");
    }

    // ---- F3: only the app page itself may drive the host

    [Theory]
    [InlineData("https://app.local/index.html", true)]
    [InlineData("https://APP.LOCAL/index.html#x", true)]
    [InlineData("https://app.local/index.html?v=2", true)]
    [InlineData("https://app.local/other.html", false)]
    [InlineData("https://app.local/lib/monaco/vs/editor.html", false)]
    [InlineData("https://app.local/", false)]
    [InlineData("https://app.local.example.com/index.html", false)]
    [InlineData("file:///C:/Users/x/Desktop/page.html", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_app_page_is_trusted(string? uri, bool trusted) =>
        Assert.Equal(trusted, MainForm.IsAppUri(uri));
}
