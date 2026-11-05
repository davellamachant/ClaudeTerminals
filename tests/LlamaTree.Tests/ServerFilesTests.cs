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
    // Windows drops dots and spaces from the end of a name as it makes the file: the name kept is the file made (C1).
    [InlineData("README.cmd .", "README.cmd")]
    [InlineData("x.exe  .", "x.exe")]
    [InlineData("x.cmd . .", "x.cmd")]
    [InlineData("x.bat.\t", "x.bat._")]                                      // a tab is a control character
    [InlineData("  setup.msi \u00a0", "setup.msi")]
    [InlineData("link.lnk...", "link.lnk")]
    [InlineData(" . . ", "file")]
    [InlineData("...", "file")]
    [InlineData("notes.txt:Zone.Identifier", "notes.txt_Zone.Identifier")]   // no alternate data streams
    [InlineData("evil\u202etxt.exe", "evil_txt.exe")]                       // no right-to-left override hiding .exe
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("com1.log", "_com1.log")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("AUX .", "_AUX")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("com10.txt", "com10.txt")]
    public void A_name_from_the_other_computer_is_only_ever_a_file_name(string name, string expected) =>
        Assert.Equal(expected, ServerFiles.SafeName(name));

    [Fact]
    public void A_name_made_safe_stays_as_it_is_when_made_safe_again()
    {
        foreach (var name in new[] { "README.cmd .", "x.exe  .", "x.cmd . .", "CON", " a . b . ", "a.txt" })
            Assert.Equal(ServerFiles.SafeName(name), ServerFiles.SafeName(ServerFiles.SafeName(name)));
    }

    [Fact]
    public void A_file_named_to_hide_its_type_is_kept_under_the_name_it_really_has()
    {
        var copies = new ServerCopies(root);
        copies.ExpectFile("/srv/work/README.cmd .");
        var path = copies.FilePart("tok1", "README.cmd .", Part("@echo off"), 0, true);
        Assert.Equal("README.cmd", Path.GetFileName(path));
        Assert.True(File.Exists(path));
        Assert.False(LlamaTree.Platform.Desktop.IsViewable(path!));   // so it isn't handed to the desktop to open
    }

    [Fact]
    public void A_file_that_comes_must_be_the_one_asked_for()
    {
        var copies = new ServerCopies(root);
        copies.ExpectFile("/home/me/work/notes.txt");
        // Another name than asked: refused, and the request still waits for its own file.
        Assert.Null(copies.FilePart("t1", "notes.cmd", Part("x"), 0, true));
        Assert.False(Directory.Exists(Path.Combine(root, "opened", "t1")));
        var path = copies.FilePart("t3", "notes.txt", Part("x"), 0, true);
        Assert.Equal(Path.Combine(root, "opened", "t3", "notes.txt"), path);
        // That request is answered: another file of its name is unasked.
        Assert.Null(copies.FilePart("t4", "notes.txt", Part("x"), 0, true));

        // A Windows server's path, and two requests answered in either order.
        copies.ExpectFile(@"C:\work\a.md");
        copies.ExpectFile("src/b.cs");
        Assert.Equal("b.cs", Path.GetFileName(copies.FilePart("t5", "b.cs", Part("x"), 0, true)));
        Assert.Equal("a.md", Path.GetFileName(copies.FilePart("t6", "a.md", Part("x"), 0, true)));
    }

    [WindowsFact]
    public void A_servers_file_is_marked_as_from_the_internet()
    {
        var copies = new ServerCopies(root);
        copies.ExpectFile("report.pdf");
        var path = copies.FilePart("motw", "report.pdf", Part("%PDF"), 0, true)!;
        Assert.Equal("%PDF", File.ReadAllText(path));
        Assert.Contains("ZoneId=3", File.ReadAllText(path + ":Zone.Identifier"));
        // Where it can't be marked, nothing fails.
        ServerCopies.MarkFromInternet(Path.Combine(root, "missing", "x.txt"));
    }

    [LinuxUserXattrFact]
    public void On_Linux_a_servers_file_records_where_it_came_from()
    {
        var copies = new ServerCopies(root, "dave@build.example");
        copies.ExpectFile("report.pdf");
        var path = copies.FilePart("origin", "report.pdf", Part("%PDF"), 0, true)!;
        Assert.Equal("%PDF", File.ReadAllText(path));
        Assert.Equal("llamatree-server://build.example/report.pdf", Platform.ExtendedAttributes.TryGet(path, "user.xdg.origin.url"));
        // It's still made read-only after.
        Assert.False(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserWrite));
    }

    [UnixFact]
    public void Where_a_file_cant_be_marked_nothing_fails()
    {
        ServerCopies.MarkFromInternet(Path.Combine(root, "missing", "x.txt"), "llamatree-server://s/x.txt");
        Assert.False(Platform.ExtendedAttributes.TrySet(Path.Combine(root, "missing", "x.txt"), "user.test", "x"));
        Assert.Null(Platform.ExtendedAttributes.TryGet(Path.Combine(root, "missing", "x.txt"), "user.test"));
    }

    [Fact]
    public void The_macOS_quarantine_value_says_downloaded_and_unapproved()
    {
        var at = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var fields = ServerCopies.QuarantineValue(at).Split(';');
        Assert.Equal(4, fields.Length);
        Assert.Equal("0081", fields[0]);                                               // in quarantine, downloaded
        Assert.Matches("^[0-9a-f]{8}$", fields[1]);
        Assert.Equal(at.ToUnixTimeSeconds(), Convert.ToInt64(fields[1], 16));
        Assert.Equal("LlamaTree", fields[2]);
        Assert.Equal("", fields[3]);                                                   // no QuarantineEvents entry
    }

    [Fact]
    public void Where_a_file_came_from_is_a_url_of_the_server_and_its_name()
    {
        Assert.Equal("llamatree-server://host/a%20b%23c.txt", ServerCopies.OriginUrl("me@host", "a b#c.txt"));
        Assert.Equal("llamatree-server://build-box/x.txt", ServerCopies.OriginUrl("build-box", "x.txt"));
        Assert.Equal("llamatree-server:///server/x.txt", ServerCopies.OriginUrl(null, "x.txt"));
        Assert.Equal("llamatree-server:///my%20box/x.txt", ServerCopies.OriginUrl("me@my box", "x.txt"));
        foreach (var server in new[] { "me@host", "build-box", "me@my box", "a@b@c:22", "" })
        {
            Assert.True(Uri.TryCreate(ServerCopies.OriginUrl(server, "y z.txt"), UriKind.Absolute, out var url), server);
            Assert.Equal("llamatree-server", url.Scheme);
            Assert.DoesNotContain("me", url.AbsoluteUri.Replace("llamatree-server", ""));   // no user name
        }
    }

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
        // The folder as resolved (on a Mac the temporary folder /var/... is really /private/var/...).
        Assert.Equal(LlamaTree.Platform.PathText.ResolvedPath(Path.Combine(allowed, "src")), LlamaTree.Platform.PathText.ResolvedInside(Path.Combine(allowed, "src"), allowed));
        Assert.Null(LlamaTree.Platform.PathText.ResolvedInside(outside, allowed));
        var link = Path.Combine(allowed, "out");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // Windows without the right to make links
        Assert.Null(LlamaTree.Platform.PathText.ResolvedInside(link, allowed));
        Assert.Null(LlamaTree.Platform.PathText.ResolvedInside(Path.Combine(link, "."), allowed));
    }

    [Fact]
    public void A_plugin_of_endless_empty_files_is_dropped()
    {
        var copies = new ServerCopies(root);
        copies.PluginBegin("flood");
        for (var i = 0; i <= ServerCopies.MaxPluginFiles + 10; i++) copies.PluginPart("flood", $"f{i}.txt", "", 0, true);
        Assert.False(Directory.Exists(copies.PluginFolder("flood")));
        // Nothing more of it is taken once it's gone.
        copies.PluginPart("flood", "late.txt", Part("x"), 0, true);
        Assert.False(Directory.Exists(copies.PluginFolder("flood")));
    }

    [Fact]
    public void A_folder_isnt_inside_another_that_differs_only_in_letter_case_nor_is_anything_inside_root_refused()
    {
        var upper = Path.Combine(root, "Project");
        var lower = Path.Combine(root, "project");
        Directory.CreateDirectory(upper);
        if (Directory.Exists(lower) && LlamaTree.Platform.PathText.FinalPath(lower) == LlamaTree.Platform.PathText.FinalPath(upper))
        {
            // Not case sensitive here (Windows, a Mac's usual volume): the one folder is spelled as the system has it.
            Assert.NotNull(LlamaTree.Platform.PathText.ResolvedInside(Path.Combine(lower, "."), upper));
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(lower, "private"));
            Assert.Null(LlamaTree.Platform.PathText.ResolvedInside(Path.Combine(lower, "private"), upper));
        }
        // The root of the file system holds everything.
        var top = Path.GetPathRoot(root)!;
        Assert.NotNull(LlamaTree.Platform.PathText.ResolvedInside(root, top));
    }

    [Theory]
    [InlineData("NEEDS_PASSWORD apt x64", "curl -fsSL -o llamatree.deb 'https://github.com/x/llamatree_1.3.0_amd64.deb' && echo 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  llamatree.deb' | sha256sum -c && sudo apt-get install -y ./llamatree.deb")]
    [InlineData("NEEDS_PASSWORD dnf arm64", "curl -fsSL -o llamatree.rpm 'https://github.com/x/llamatree-1.3.0-1.aarch64.rpm' && echo 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  llamatree.rpm' | sha256sum -c && sudo dnf install -y ./llamatree.rpm")]
    [InlineData("NEEDS_PASSWORD https://evil/x';rm -rf ~;' 0000 apt-get install -y", null)]   // the old form, forged
    [InlineData("NEEDS_PASSWORD apt x64; rm -rf ~", null)]
    [InlineData("NEEDS_PASSWORD pacman x64", null)]
    [InlineData("NEEDS_PASSWORD apt riscv64", null)]
    [InlineData("NEEDS_PASSWORD zypper x64", null)]   // no such file in this release
    [InlineData("", null)]
    public void The_command_shown_for_a_sudo_password_comes_only_from_this_computers_values(string said, string? expected)
    {
        var files = new Dictionary<string, (string Url, string Sha256)>
        {
            ["llamatree_1.3.0_amd64.deb"] = ("https://github.com/x/llamatree_1.3.0_amd64.deb", new string('a', 64)),
            ["llamatree-1.3.0-1.aarch64.rpm"] = ("https://github.com/x/llamatree-1.3.0-1.aarch64.rpm", new string('b', 64)),
        };
        Assert.Equal(expected, ServerUpdate.PasswordCommand(said, new Version(1, 3, 0), files));
    }

    [Fact]
    public void The_session_a_server_made_is_the_one_it_names()
    {
        static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;
        string? Find(string id) => id is "r1" or "r2" ? "local-" + id : null;
        // It says which: that one, whatever else appeared meanwhile (another add, someone else's session).
        Assert.Equal("local-r2", AppHost.StartedOnServer(Json("""["r2"]"""), Find, ["local-r1", "local-r2", "local-x"]));
        Assert.Null(AppHost.StartedOnServer(Json("""["gone"]"""), Find, ["local-r1"]));
        Assert.Null(AppHost.StartedOnServer(Json("""["r1","r2"]"""), Find, ["local-r1"]));
        // An older server says nothing: the one that appeared, or none when two did (no guessing).
        Assert.Equal("local-r1", AppHost.StartedOnServer(Json("null"), Find, ["local-r1"]));
        Assert.Null(AppHost.StartedOnServer(Json("null"), Find, ["local-r1", "local-x"]));
        Assert.Null(AppHost.StartedOnServer(Json("null"), Find, Array.Empty<string>()));
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

/// <summary>A test of Linux user extended attributes: skipped elsewhere, and where the temp folder's file system has none (tmpfs, some overlays).</summary>
public sealed class LinuxUserXattrFactAttribute : FactAttribute
{
    public LinuxUserXattrFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only";
            return;
        }
        var probe = Path.Combine(Path.GetTempPath(), "lt-xattr-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(probe, "");
            if (!LlamaTree.Platform.ExtendedAttributes.TrySet(probe, "user.llamatree.probe", "1")) Skip = "The temp folder's file system has no user extended attributes";
        }
        finally { File.Delete(probe); }
    }
}
