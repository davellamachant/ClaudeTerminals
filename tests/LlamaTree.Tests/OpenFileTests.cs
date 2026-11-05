using LlamaTree.Git;

namespace LlamaTree.Tests;

/// <summary>"Open" / "Open file" (review finding F2): never run a file, never leave the repository.</summary>
public sealed class OpenFileTests
{
    private const string Root = @"C:\work\repo";

    [WindowsTheory]
    [InlineData("a.txt", @"C:\work\repo\a.txt")]
    [InlineData("sub/b.cs", @"C:\work\repo\sub\b.cs")]
    [InlineData(@"sub\b.cs", @"C:\work\repo\sub\b.cs")]
    [InlineData("sub/../c.md", @"C:\work\repo\c.md")]
    [InlineData("spa ce & ünï.txt", @"C:\work\repo\spa ce & ünï.txt")]
    public void Paths_inside_the_repository_resolve(string relative, string expected) =>
        Assert.Equal(expected, GitService.FullPathInRepo(Root, relative));

    [WindowsTheory]
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
        Assert.True(LlamaTree.Platform.Desktop.IsRunnable(file));

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("README.md")]
    [InlineData("notes.txt")]
    [InlineData("package.json")]
    [InlineData("icon.png")]
    [InlineData("Makefile")]
    public void Documents_are_not_runnable(string file) =>
        Assert.False(LlamaTree.Platform.Desktop.IsRunnable(file));

    // C1: a name ending in dots or spaces is made by Windows without them, so it runs as what's before them.
    [WindowsTheory]
    [InlineData("x.cmd ")]
    [InlineData("README.cmd .")]
    [InlineData("x.exe  .")]
    [InlineData("x.cmd . .")]
    [InlineData("x.bat.")]
    [InlineData("x.lnk ")]
    [InlineData("x.msi. ")]
    [InlineData(@"C:\work\x.ps1 ")]
    public void A_name_ending_in_dots_or_spaces_is_judged_by_the_file_windows_makes(string file) =>
        Assert.True(LlamaTree.Platform.Desktop.IsRunnable(file));

    // M3: types that do something when opened, though they aren't programs.
    [WindowsTheory]
    [InlineData("x.search-ms")]
    [InlineData("x.searchConnector-ms")]
    [InlineData("x.library-ms")]
    [InlineData("x.xll")]
    [InlineData("x.docm")]
    [InlineData("x.xlsm")]
    [InlineData("x.diagcab")]
    [InlineData("x.rdp")]
    [InlineData("x.vsto")]
    [InlineData("x.jnlp")]
    [InlineData("x.ms-appinstaller")]
    [InlineData("x.html")]
    [InlineData("x.mht")]
    [InlineData("x.lnk")]
    [InlineData("x.url")]
    public void Types_that_act_when_opened_count_as_runnable(string file) =>
        Assert.True(LlamaTree.Platform.Desktop.IsRunnable(file));

    // What a server sends goes to its app only when it's a type that's shown, not run (on every system).
    [Theory]
    [InlineData("x.cmd ")]
    [InlineData("README.cmd .")]
    [InlineData("x.exe  .")]
    [InlineData("x.cmd . .")]
    [InlineData("photo.png.")]
    [InlineData("notes.txt ")]
    [InlineData("x.cmd")]
    [InlineData("x.bat")]
    [InlineData("x.exe")]
    [InlineData("x.lnk")]
    [InlineData("x.msi")]
    [InlineData("x.search-ms")]
    [InlineData("x.xll")]
    [InlineData("x.docm")]
    [InlineData("x.xlsm")]
    [InlineData("x.diagcab")]
    [InlineData("x.rdp")]
    [InlineData("x.vsto")]
    [InlineData("x.jnlp")]
    [InlineData("x.ms-appinstaller")]
    [InlineData("x.html")]
    [InlineData("x.htm")]
    [InlineData("x.mht")]
    [InlineData("x.svg")]
    [InlineData("x.js")]
    [InlineData("x.py")]
    [InlineData("x.csv")]
    [InlineData("x.docx")]
    [InlineData("Makefile")]
    [InlineData("x")]
    public void A_servers_file_of_any_other_type_is_not_handed_to_its_app(string file) =>
        Assert.False(LlamaTree.Platform.Desktop.IsViewable(file));

    [Theory]
    [InlineData("photo.png")]
    [InlineData("Photo.JPG")]
    [InlineData("report.pdf")]
    [InlineData("README.md")]
    [InlineData("notes.txt")]
    [InlineData("build.log")]
    [InlineData("package.json")]
    [InlineData("Program.cs")]
    public void A_servers_picture_document_or_text_is_handed_to_its_app(string file) =>
        Assert.True(LlamaTree.Platform.Desktop.IsViewable(file));

    [Fact]
    public void Only_a_file_without_nul_bytes_looks_like_text()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lt-text-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.cmd"), "@echo off\r\n");
            File.WriteAllBytes(Path.Combine(dir, "b.exe"), [0x4D, 0x5A, 0x90, 0x00, 0x03]);
            Assert.True(LlamaTree.Platform.Desktop.LooksLikeText(Path.Combine(dir, "a.cmd")));
            Assert.False(LlamaTree.Platform.Desktop.LooksLikeText(Path.Combine(dir, "b.exe")));
            Assert.False(LlamaTree.Platform.Desktop.LooksLikeText(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // An extension on none of the lists, associated on some machine with a program that runs it.
    [WindowsTheory]
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
        Assert.Equal(runs, LlamaTree.Platform.Desktop.DefaultActionRuns(command, program));

    [WindowsFact]
    public void This_machines_cmd_association_counts_as_running() =>
        Assert.True(LlamaTree.Platform.Desktop.DefaultActionRuns(
            LlamaTree.Terminal.Native.AssocQuery(LlamaTree.Terminal.Native.ASSOCSTR_COMMAND, ".cmd"),
            LlamaTree.Terminal.Native.AssocQuery(LlamaTree.Terminal.Native.ASSOCSTR_EXECUTABLE, ".cmd")));

    [WindowsFact]
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
            FileSystemFixtures.Junction(Path.Combine(repo, "link"), outside);
            FileSystemFixtures.Junction(alias, repo);
            FileSystemFixtures.Junction(Path.Combine(top, "hop2"), outside);
            FileSystemFixtures.Junction(Path.Combine(repo, "hop1"), Path.Combine(top, "hop2"));   // a chain that ends outside
            FileSystemFixtures.Junction(Path.Combine(repo, "self"), Path.Combine(repo));          // a link that stays inside

            Assert.NotNull(GitService.FullPathInRepo(repo, @"link\secret.txt"));        // lexically inside...
            Assert.Null(GitService.ExistingPathInRepo(repo, @"link\secret.txt"));        // ...but really outside
            Assert.Null(GitService.ExistingPathInRepo(repo, @"hop1\secret.txt"));
            Assert.NotNull(GitService.ExistingPathInRepo(repo, "inside.txt"));
            Assert.NotNull(GitService.ExistingPathInRepo(repo, "INSIDE.TXT"));          // case variant in a case-insensitive folder
            // Links that stay inside are followed, where Windows goes through them: newer builds refuse a junction they
            // don't trust ("an untrusted mount point"), and then nothing can open a file through it.
            if (File.Exists(Path.Combine(repo, @"self\inside.txt")))
            {
                Assert.NotNull(GitService.ExistingPathInRepo(repo, @"self\inside.txt"));
                Assert.NotNull(GitService.ExistingPathInRepo(alias, "inside.txt"));      // repository opened through a link
            }
            Assert.Null(GitService.ExistingPathInRepo(repo, "missing.txt"));
        }
        finally
        {
            FileSystemFixtures.DeleteTree(top);   // removes the junctions, not their targets' contents twice
        }
    }

    [WindowsFact]
    public void A_differently_cased_sibling_folder_is_outside_the_repository()
    {
        // In a case-sensitive parent, Repo and repo are two folders; text equal after case folding is not "inside".
        var top = Path.Combine(Path.GetTempPath(), "ct-casepar-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            FileSystemFixtures.MakeCaseSensitive(top);
            var repo = Path.Combine(top, "Repo");
            var sibling = Path.Combine(top, "repo");
            Directory.CreateDirectory(repo);
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(repo, "inside.txt"), "in");
            File.WriteAllText(Path.Combine(sibling, "secret.txt"), "out");
            FileSystemFixtures.Junction(Path.Combine(repo, "link"), sibling);

            Assert.Null(GitService.FullPathInRepo(repo, @"..\repo\secret.txt"));
            Assert.Null(GitService.ExistingPathInRepo(repo, @"link\secret.txt"));
            Assert.NotNull(GitService.ExistingPathInRepo(repo, "inside.txt"));
        }
        finally
        {
            FileSystemFixtures.DeleteTree(top);
        }
    }

#if WINFORMS
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
#endif
}
