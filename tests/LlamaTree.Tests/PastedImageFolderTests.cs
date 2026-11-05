using LlamaTree.Platform;

namespace LlamaTree.Tests;

/// <summary>
/// Images pasted into a terminal are saved where only you can reach them (never the shared temp folder another account
/// could make first), and the week-old clear-out deletes only the app's own images, never through a link.
/// </summary>
public class PastedImageFolderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lt-paste-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string data, runtime;

    public PastedImageFolderTests()
    {
        data = Path.Combine(root, "data");
        runtime = Path.Combine(root, "run");
        Directory.CreateDirectory(data);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(runtime);
        else Directory.CreateDirectory(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Without_a_runtime_folder_images_go_in_the_data_folder()
    {
        var path = Desktop.NewPastedImagePath(null, data, Now);
        Assert.Equal(Path.Combine(data, "pasted-images", "image-20261105-120000-000.png"), path);
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [UnixFact]
    public void A_private_runtime_folder_is_used_first()
    {
        var path = Desktop.NewPastedImagePath(runtime, data, Now);
        Assert.Equal(Path.Combine(runtime, "LlamaTree", "pasted-images", "image-20261105-120000-000.png"), path);
    }

    [UnixFact]
    public void A_runtime_folder_others_can_reach_or_a_relative_one_isnt_used()
    {
        File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Assert.Equal([Path.Combine(data, "pasted-images")], Desktop.PastedImageFolders(runtime, data));
        Assert.Equal([Path.Combine(data, "pasted-images")], Desktop.PastedImageFolders("run", data));
        Assert.Equal([Path.Combine(data, "pasted-images")], Desktop.PastedImageFolders(Path.Combine(root, "missing"), data));
    }

    [WindowsFact]
    public void Windows_ignores_a_runtime_folder()
    {
        Assert.Equal([Path.Combine(data, "pasted-images")], Desktop.PastedImageFolders(runtime, data));
    }

    [UnixFact]
    public void Folders_are_0700_and_images_0600()
    {
        var path = Desktop.NewPastedImagePath(runtime, data, Now);
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(Private, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(runtime, "LlamaTree")));
        using (var file = Desktop.CreatePastedImage(path)) file.WriteByte(1);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [UnixFact]
    public void A_folder_of_yours_others_could_read_is_closed_up()
    {
        var folder = Path.Combine(data, "pasted-images");
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Assert.True(Desktop.MakePrivateFolder(folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
    }

    [UnixFact]
    public void A_folder_others_can_write_to_is_refused()
    {
        var parent = Path.Combine(runtime, "LlamaTree");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, (UnixFileMode)0b111_111_111);   // as another account could leave it
        Assert.False(Desktop.MakePrivateFolder(Path.Combine(parent, "pasted-images")));
        Assert.False(Directory.Exists(Path.Combine(parent, "pasted-images")));   // nothing made in it
    }

    [UnixFact]
    public void A_linked_folder_or_parent_is_refused_and_nothing_is_written_through_it()
    {
        var pictures = Path.Combine(root, "Pictures");
        Directory.CreateDirectory(pictures);
        var old = Path.Combine(pictures, "image-20200101-000000-000.png");
        File.WriteAllBytes(old, [1]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

        // The folder itself a link to your pictures.
        var parent = Path.Combine(runtime, "LlamaTree");
        Directory.CreateDirectory(parent);
        Directory.CreateSymbolicLink(Path.Combine(parent, "pasted-images"), pictures);
        Assert.False(Desktop.MakePrivateFolder(Path.Combine(parent, "pasted-images")));
        // Refused there, the data folder is used instead.
        Assert.StartsWith(Path.Combine(data, "pasted-images"), Desktop.NewPastedImagePath(runtime, data, Now));

        // The parent a link.
        var linkedParent = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(linkedParent, pictures);
        Assert.False(Desktop.MakePrivateFolder(Path.Combine(linkedParent, "pasted-images")));

        Assert.True(File.Exists(old));
        Assert.Equal([old], Directory.GetFileSystemEntries(pictures));
    }

    [UnixFact]
    public void With_no_safe_folder_the_paste_fails()
    {
        var pictures = Path.Combine(root, "Pictures");
        Directory.CreateDirectory(pictures);
        Directory.CreateSymbolicLink(Path.Combine(data, "pasted-images"), pictures);
        Assert.Throws<IOException>(() => Desktop.NewPastedImagePath(null, data, Now));
    }

    [Fact]
    public void The_clear_out_deletes_only_old_images_the_app_named()
    {
        var folder = Path.Combine(data, "pasted-images");
        Directory.CreateDirectory(folder);
        string Old(string name)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
            return path;
        }
        var ours = Old("image-20200101-000000-000.png");
        var recent = Path.Combine(folder, "image-20261105-115959-000.png");
        File.WriteAllBytes(recent, [1]);
        var other = Old("holiday.png");
        var near = Old("image-20200101-000000-000.png.png");
        var sub = Path.Combine(folder, "image-20200101-000000-001.png");
        Directory.CreateDirectory(sub);

        Desktop.ClearOldPastedImages(folder, DateTime.UtcNow.AddDays(-7));

        Assert.False(File.Exists(ours));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(near));
        Assert.True(Directory.Exists(sub));
    }

    [UnixFact]
    public void The_clear_out_leaves_links_and_what_they_lead_to()
    {
        var folder = Path.Combine(data, "pasted-images");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(root, "precious.png");
        File.WriteAllBytes(target, [1]);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-30));
        var link = Path.Combine(folder, "image-20200101-000000-000.png");
        File.CreateSymbolicLink(link, target);

        Desktop.ClearOldPastedImages(folder, DateTime.UtcNow.AddDays(-7));

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void An_image_file_is_always_new()
    {
        var path = Desktop.NewPastedImagePath(null, data, Now);
        File.WriteAllBytes(path, [1]);
        Assert.Throws<IOException>(() => Desktop.CreatePastedImage(path).Dispose());
    }
}
