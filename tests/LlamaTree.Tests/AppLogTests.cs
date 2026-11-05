using System.Text.Json.Nodes;
using LlamaTree.Hosting;
using LlamaTree.Hosting.Link;

namespace LlamaTree.Tests;

/// <summary>These set the process's log (AppLog.Current), so they don't run alongside each other.</summary>
[CollectionDefinition("AppLog", DisableParallelization = true)]
public sealed class AppLogCollection;

[Collection("AppLog")]
public class AppLogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lt-log-" + Guid.NewGuid().ToString("N")[..8]);

    public AppLogTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        AppLog.Current = null;
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private string Logs => Path.Combine(root, "logs");

    [Fact]
    public void A_log_past_its_size_moves_to_dot_1_and_starts_again()
    {
        var log = new AppLog(Logs, "host", maxBytes: 1000);
        for (var i = 0; i < 30; i++) log.Write("INFO", $"entry {i} " + new string('x', 50));
        Assert.True(File.Exists(log.FilePath + ".1"));
        Assert.InRange(new FileInfo(log.FilePath).Length, 1, 1000);
        Assert.InRange(new FileInfo(log.FilePath + ".1").Length, 1, 1000);
        // The newest is in the file, the ones before in .1; only one .1 is kept.
        Assert.EndsWith("entry 29 " + new string('x', 50), File.ReadAllLines(log.FilePath).Last());
        Assert.Equal(["llamatree-host.log", "llamatree-host.log.1"], Directory.GetFiles(Logs).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Each_entry_is_one_line_with_no_control_characters()
    {
        var log = new AppLog(Logs, "app");
        log.Write("WARN", "first\r\nERROR forged entry\u001b[31m red\u2028sep\0end");
        log.Write("INFO", new string('y', 5000));
        var lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z app WARN first  ERROR forged entry \[31m red sep end$", lines[0]);
        Assert.DoesNotContain(lines[0], c => char.IsControl(c) || c == '\u2028');
        // A long entry is clipped.
        Assert.True(lines[1].Length < AppLog.MaxMessage + 40);
        Assert.EndsWith("…", lines[1]);
    }

    [Fact]
    public void Writing_never_throws()
    {
        // Its folder can't be made: a file is where it would be.
        File.WriteAllText(Logs, "");
        new AppLog(Logs, "app").Write("ERROR", "nowhere to go");
        Assert.Null(Record.Exception(() => AppLog.Warn("no log started")));
    }

    [Fact]
    public void A_dropped_server_message_is_logged_by_its_type_never_what_it_said()
    {
        AppLog.Current = new AppLog(Logs, "host");
        var c = new ServerConnection("dev@box", a => a(), Path.Combine(root, "copies"));
        using var link = new LinkChannel(new MemoryStream());
        c.PluginsChanged += _ => throw new InvalidOperationException("a handler that fails");
        const string secret = "ghp_SECRETTOKEN1234567890";
        var json = $$"""{"op":"serve","id":"tasks","token":"{{secret}}","data":"file contents here"}""";
        Assert.Equal("", c.WindowOp(link, json));
        var text = File.ReadAllText(AppLog.Current.FilePath);
        Assert.Contains("dev@box: a serve message (" + json.Length + " characters) failed and was dropped: InvalidOperationException: a handler that fails", text);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("file contents", text);
    }

    [Fact]
    public void A_messages_type_is_kept_to_a_safe_word()
    {
        Assert.Equal("git.status", AppLog.MessageType("""{"type":"git.status","diff":"secret"}"""));
        Assert.Equal("evilline", AppLog.MessageType("""{"type":"evil\nline"}"""));
        Assert.Equal("?", AppLog.MessageType("""{"data":"secret"}"""));
        Assert.Equal("?", AppLog.MessageType("not json"));
        Assert.Equal(40, AppLog.MessageType($$"""{"type":"{{new string('a', 500)}}"}""").Length);
    }

    [Fact]
    public void A_refused_file_is_logged()
    {
        AppLog.Current = new AppLog(Logs, "host");
        var copies = new ServerCopies(Path.Combine(root, "copies"), "dev@box");
        Assert.Null(copies.FilePart("abc123", "notes.txt", Convert.ToBase64String("secret"u8.ToArray()), 0, true));   // nobody asked for it
        var text = File.ReadAllText(AppLog.Current.FilePath);
        Assert.Contains("dev@box: a file to open was refused: nothing here asked to open one.", text);
        Assert.DoesNotContain("notes.txt", text);
    }

    [Fact]
    public void A_server_with_too_many_sessions_is_noted_once_a_connection()
    {
        static JsonArray List(int n) => new(Enumerable.Range(0, n).Select(i => (JsonNode?)new JsonObject { ["id"] = $"r{i}" }).ToArray());
        var c = new ServerConnection("dev@box", a => a(), Path.Combine(root, "copies"));
        var few = List(AppHost.MaxServerSessions);
        Assert.Null(AppHost.OverCap(c, few, AppHost.ServerSessionsShown(few, new HashSet<string>())));   // exactly as many: nothing to say
        var many = List(AppHost.MaxServerSessions + 20);
        var shown = AppHost.ServerSessionsShown(many, new HashSet<string>());
        Assert.Equal(AppHost.MaxServerSessions + 20, AppHost.OverCap(c, many, shown));
        // Every sync after says nothing more on this connection.
        Assert.Null(AppHost.OverCap(c, many, shown));
        Assert.Null(AppHost.OverCap(c, many, shown));
    }

    [UnixFact]
    public void On_unix_the_folder_and_files_are_only_their_owners()
    {
        var log = new AppLog(Logs, "bridge", maxBytes: 200);
        for (var i = 0; i < 10; i++) log.Write("INFO", "entry " + new string('z', 40));
        if (OperatingSystem.IsWindows()) return;   // for the analyser: UnixFact skips it there
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Logs));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(log.FilePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(log.FilePath + ".1"));
    }
}
