using System.Text.Json;
using LlamaTree.Hosting;

namespace LlamaTree.Tests;

public class ServerFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lt-files-" + Guid.NewGuid().ToString("N")[..8]);

    public ServerFilesTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
            Directory.Delete(root, true);
        }
        catch (IOException) { }
    }

    private static string Part(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\boot.ini", "boot.ini")]
    [InlineData("..", "file")]
    [InlineData("", "file")]
    [InlineData("a\u0001b.png", "a_b.png")]
    public void A_name_from_the_other_computer_is_only_ever_a_file_name(string name, string expected) =>
        Assert.Equal(expected, ServerFiles.SafeName(name));

    [Fact]
    public void A_typed_path_starts_from_the_home_folder()
    {
        Assert.Equal(root, ServerFiles.Resolve("", root));
        Assert.Equal(root, ServerFiles.Resolve("~", root));
        Assert.Equal(Path.Combine(root, "work"), ServerFiles.Resolve("~/work", root));
        Assert.Equal(Path.Combine(root, "work"), ServerFiles.Resolve("work", root));
    }

    [Fact]
    public void A_folder_lists_its_folders_then_its_files()
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "b.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "A.md"), "");
        var listing = JsonSerializer.SerializeToElement(ServerFiles.List(root), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var entries = listing.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("name").GetString(), e.GetProperty("folder").GetBoolean(), e.GetProperty("hidden").GetBoolean())).ToList();
        Assert.Equal([(".git", true, true), ("src", true, false), ("A.md", false, false), ("b.txt", false, false)], entries);
        Assert.Equal(root, listing.GetProperty("path").GetString());
        Assert.Throws<InvalidOperationException>(() => ServerFiles.List(Path.Combine(root, "missing")));
    }

    [Fact]
    public void A_file_received_in_parts_is_put_together_and_its_path_given()
    {
        var copies = new ServerCopies(root);
        Assert.Null(copies.FilePart("abc123", "../report.txt", Part("one "), 0, false));
        var path = copies.FilePart("abc123", "../report.txt", Part("two"), 1, true);
        Assert.Equal(Path.Combine(root, "opened", "abc123", "report.txt"), path);
        Assert.Equal("one two", File.ReadAllText(path!));
        // A copy: saving it wouldn't change the server's file, so it can't be saved.
        if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(path!).HasFlag(FileAttributes.ReadOnly));
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead, File.GetUnixFileMode(path!));
    }

    [Fact]
    public void A_file_whose_parts_come_out_of_order_or_with_a_bad_token_is_dropped()
    {
        var copies = new ServerCopies(root);
        copies.FilePart("tok", "a.txt", Part("one"), 0, false);
        Assert.Null(copies.FilePart("tok", "a.txt", Part("three"), 2, true));
        Assert.False(File.Exists(Path.Combine(root, "opened", "tok", "a.txt")));
        Assert.Null(copies.FilePart("../x", "a.txt", Part("x"), 0, true));
        Assert.False(Directory.Exists(Path.Combine(root, "x")));
    }

    [Theory]
    [InlineData("../escape.js")]
    [InlineData("a/../../escape.js")]
    [InlineData("/etc/escape.js")]
    [InlineData("a//b.js")]
    [InlineData("C:\\escape.js")]
    public void A_plugin_file_cant_be_put_outside_its_folder(string relative)
    {
        var copies = new ServerCopies(Path.Combine(root, "copies"));
        copies.PluginBegin("tasks");
        copies.PluginPart("tasks", relative, Part("x"), 0, true);
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_plugin_is_copied_into_its_own_folder_and_starts_afresh_each_time()
    {
        var copies = new ServerCopies(root);
        copies.PluginBegin("tasks");
        copies.PluginPart("tasks", "index.html", Part("<p>hi</p>"), 0, true);
        copies.PluginPart("tasks", "lib/app.js", Part("go()"), 0, true);
        var folder = copies.PluginFolder("tasks");
        Assert.Equal("<p>hi</p>", File.ReadAllText(Path.Combine(folder, "index.html")));
        Assert.Equal("go()", File.ReadAllText(Path.Combine(folder, "lib", "app.js")));
        copies.PluginBegin("tasks");
        Assert.False(File.Exists(Path.Combine(folder, "lib", "app.js")));
        copies.PluginPart("Not An Id", "index.html", Part("x"), 0, true);
        Assert.False(Directory.Exists(copies.PluginFolder("Not An Id")));
    }

    [Fact]
    public void A_pasted_file_arrives_under_its_own_name_and_a_half_sent_one_is_deleted()
    {
        var uploads = new ServerFiles.Uploads(root);
        uploads.Begin("t1", "../shot.png");
        uploads.Write("t1", Part("png"));
        var path = uploads.End("t1");
        Assert.Equal("shot.png", Path.GetFileName(path));
        Assert.StartsWith(root, path);
        Assert.Equal("png", File.ReadAllText(path));

        uploads.Begin("t2", "shot.png");
        var second = Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Single(f => f != path);
        Assert.Equal("shot (2).png", Path.GetFileName(second));
        uploads.AbandonAll();
        Assert.False(File.Exists(second));
        Assert.Throws<InvalidOperationException>(() => uploads.End("t2"));
    }
}
