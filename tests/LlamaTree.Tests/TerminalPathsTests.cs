using LlamaTree.Platform;

namespace LlamaTree.Tests;

/// <summary>Paths written in a terminal: which are links, and to what.</summary>
public sealed class TerminalPathsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("llamatree-paths-").FullName;

    public TerminalPathsTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "My Folder"));
        File.WriteAllText(Path.Combine(root, "src", "app.js"), "");
        File.WriteAllText(Path.Combine(root, "README.md"), "");
    }

    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("src/app.js")]
    [InlineData("./src/app.js")]
    [InlineData("README.md")]
    public void A_relative_path_is_from_the_sessions_folder(string text)
    {
        var found = TerminalPaths.Resolve(text, root);
        Assert.NotNull(found);
        Assert.False(found.IsFolder);
        Assert.True(File.Exists(found.Path));
        Assert.True(Path.IsPathFullyQualified(found.Path));
    }

    [Fact]
    public void An_absolute_path_and_a_folder_with_spaces_are_found()
    {
        var folder = TerminalPaths.Resolve(Path.Combine(root, "src", "My Folder"), "/somewhere/else");
        Assert.NotNull(folder);
        Assert.True(folder.IsFolder);
    }

    [Theory]
    [InlineData("src/missing.js")]
    [InlineData("")]
    [InlineData("1.2.3")]
    public void Nothing_there_is_no_link(string text) => Assert.Null(TerminalPaths.Resolve(text, root));

    [WindowsTheory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData(@"\\?\C:\Windows\notepad.exe")]
    [InlineData(@"\\.\C:\Windows\notepad.exe")]
    [InlineData(@"C:Windows\notepad.exe")]                 // relative to drive C:'s own current folder
    [InlineData(@"C:\Windows\win.ini:hidden")]             // an alternate data stream
    public void Network_device_and_odd_windows_paths_are_never_looked_at(string text) =>
        Assert.Null(TerminalPaths.Resolve(text, @"C:\"));

    [WindowsFact]
    public void A_relative_path_climbing_to_a_network_share_is_refused() =>
        Assert.Null(TerminalPaths.Resolve(@"..\..\x.txt", @"\\server\share\a\b"));

    [Fact]
    public void Tilde_is_the_home_folder()
    {
        var home = TerminalPaths.Resolve("~", root);
        Assert.NotNull(home);
        Assert.True(home.IsFolder);
        Assert.Equal(PathText.TrimEnd(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), home.Path);
    }
}
