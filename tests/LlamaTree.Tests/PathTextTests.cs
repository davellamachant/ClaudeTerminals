using LlamaTree.Platform;

namespace LlamaTree.Tests;

/// <summary>Comparing paths that reach one place by different routes, as macOS's /var does (it's /private/var).</summary>
public sealed class PathTextTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("llamatree-same-").FullName;

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void The_same_path_written_differently_is_the_same_place()
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "a")).FullName;
        Assert.True(PathText.SamePlace(folder, folder + Path.DirectorySeparatorChar));
        Assert.True(PathText.SamePlace(folder, Path.Combine(root, "a", "..", "a")));
        Assert.False(PathText.SamePlace(folder, root));
    }

    [UnixFact]
    public void A_path_through_a_link_is_the_same_place_even_once_its_folder_is_gone()
    {
        var real = Directory.CreateDirectory(Path.Combine(root, "private", "var")).FullName;
        var link = Path.Combine(root, "var");
        Directory.CreateSymbolicLink(link, real);
        var worktree = Directory.CreateDirectory(Path.Combine(real, "wt")).FullName;

        Assert.True(PathText.SamePlace(Path.Combine(link, "wt"), worktree));
        Directory.Delete(worktree);
        Assert.True(PathText.SamePlace(Path.Combine(link, "wt"), worktree));   // gone: compared through the folder above it
        Assert.False(PathText.SamePlace(Path.Combine(link, "other"), worktree));
    }
}
