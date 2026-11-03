namespace LlamaTree.Tests;

/// <summary>The data folders, moved from their name before the rename (Claude Terminals) the first time.</summary>
public sealed class AppProfileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "llamatree-profile-" + Guid.NewGuid().ToString("N")[..8]);

    public AppProfileTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void The_old_folder_is_moved_to_the_new_name_with_everything_in_it()
    {
        Directory.CreateDirectory(Path.Combine(root, "ClaudeTerminals", "plugins"));
        File.WriteAllText(Path.Combine(root, "ClaudeTerminals", "sessions.json"), "[]");

        var folder = AppProfile.Resolve(root);

        Assert.Equal(Path.Combine(root, "LlamaTree"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "sessions.json")));
        Assert.True(Directory.Exists(Path.Combine(folder, "plugins")));
        Assert.False(Directory.Exists(Path.Combine(root, "ClaudeTerminals")));
    }

    [Fact]
    public void A_new_folder_that_already_exists_is_used_and_the_old_one_left_alone()
    {
        Directory.CreateDirectory(Path.Combine(root, "LlamaTree"));
        Directory.CreateDirectory(Path.Combine(root, "ClaudeTerminals"));

        Assert.Equal(Path.Combine(root, "LlamaTree"), AppProfile.Resolve(root));
        Assert.True(Directory.Exists(Path.Combine(root, "ClaudeTerminals")));
    }

    [Fact]
    public void With_neither_folder_the_new_name_is_used() =>
        Assert.Equal(Path.Combine(root, "LlamaTree"), AppProfile.Resolve(root));

    [Fact]
    public void An_old_folder_that_cant_be_moved_is_used_where_it_is()
    {
        var old = Path.Combine(root, "ClaudeTerminals");
        Directory.CreateDirectory(old);
        using var held = new FileStream(Path.Combine(old, "sessions.json"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(old, AppProfile.Resolve(root));
    }
}
