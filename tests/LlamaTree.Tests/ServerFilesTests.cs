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
        copies.ExpectFile();
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
        copies.ExpectFile();
        copies.ExpectFile();
        copies.FilePart("tok", "a.txt", Part("one"), 0, false);
        Assert.Null(copies.FilePart("tok", "a.txt", Part("three"), 2, true));
        Assert.False(File.Exists(Path.Combine(root, "opened", "tok", "a.txt")));
        Assert.Null(copies.FilePart("../x", "a.txt", Part("x"), 0, true));
        Assert.False(Directory.Exists(Path.Combine(root, "x")));
    }

    [Fact]
    public void A_file_nobody_asked_for_is_refused_and_only_a_few_come_at_once()
    {
        var copies = new ServerCopies(root);
        // Unasked: nothing is written, nothing held open.
        Assert.Null(copies.FilePart("t1", "a.txt", Part("x"), 0, true));
        Assert.False(Directory.Exists(Path.Combine(root, "opened")));

        // Asked for one: one comes; the next is unasked again.
        copies.ExpectFile();
        Assert.NotNull(copies.FilePart("t2", "a.txt", Part("x"), 0, true));
        Assert.Null(copies.FilePart("t3", "b.txt", Part("x"), 0, true));
        Assert.False(File.Exists(Path.Combine(root, "opened", "t3", "b.txt")));

        // However many are asked for, at most four are on their way at once.
        for (var i = 0; i < 10; i++) copies.ExpectFile();
        for (var i = 0; i < 10; i++) copies.FilePart("p" + i, "big.bin", Part(""), 0, false);
        Assert.Equal(4, Directory.GetDirectories(Path.Combine(root, "opened")).Count(d => Path.GetFileName(d).StartsWith('p')));

        // A part longer than any the server sends drops its file.
        copies.DropAll();
        copies.ExpectFile();
        copies.FilePart("long", "c.txt", Part("x"), 0, false);
        Assert.Null(copies.FilePart("long", "c.txt", new string('A', 300_000), 1, true));
        Assert.False(File.Exists(Path.Combine(root, "opened", "long", "c.txt")));
    }

    [Fact]
    public void A_server_has_at_most_so_many_plugins_here()
    {
        var copies = new ServerCopies(root);
        for (var i = 0; i < ServerCopies.MaxPlugins + 5; i++)
        {
            copies.PluginBegin("p" + i);
            copies.PluginPart("p" + i, "index.html", Part("<p>x</p>"), 0, true);
        }
        Assert.Equal(ServerCopies.MaxPlugins, Directory.GetDirectories(Path.Combine(root, "plugins")).Length);
        // One it no longer has goes, which makes room.
        copies.PluginGone("p0");
        Assert.False(Directory.Exists(copies.PluginFolder("p0")));
        copies.PluginBegin("late");
        copies.PluginPart("late", "index.html", Part("<p>x</p>"), 0, true);
        Assert.True(File.Exists(Path.Combine(copies.PluginFolder("late"), "index.html")));
    }

    [Fact]
    public void A_folder_through_a_link_counts_as_inside_only_where_the_link_really_leads()
    {
        var allowed = Path.Combine(root, "project");
        var outside = Path.Combine(root, "private");
        Directory.CreateDirectory(Path.Combine(allowed, "src"));
        Directory.CreateDirectory(outside);
        Assert.Equal(Path.Combine(allowed, "src"), LlamaTree.Platform.PathText.ResolvedInside(Path.Combine(allowed, "src"), allowed), ignoreCase: true);
        Assert.Null(LlamaTree.Platform.PathText.ResolvedInside(outside, allowed));
        var link = Path.Combine(allowed, "out");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // Windows without the right to make links
        Assert.Null(LlamaTree.Platform.PathText.ResolvedInside(link, allowed));
        Assert.Null(LlamaTree.Platform.PathText.ResolvedInside(Path.Combine(link, "."), allowed));
    }

    [Fact]
    public void A_servers_plugin_never_takes_a_local_plugins_id()
    {
        var id = AppHost.ServerPluginId("dev@box", "tool");
        Assert.StartsWith("s-", id);
        Assert.Equal(50, id.Length);
        Assert.False(LlamaTree.Plugins.PluginCatalog.IsValidId(id));   // a folder here can't be named this
        Assert.NotEqual(id, AppHost.ServerPluginId("dev@other", "tool"));
        Assert.Equal(id, AppHost.ServerPluginId("DEV@box", "tool"));
        // Two long ids that start the same stay apart.
        var a = new string('a', 39) + "1";
        var b = new string('a', 39) + "2";
        Assert.NotEqual(AppHost.ServerPluginId("dev@box", a), AppHost.ServerPluginId("dev@box", b));
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
