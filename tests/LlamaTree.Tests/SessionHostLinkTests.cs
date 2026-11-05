using System.IO.Pipes;
using LlamaTree.Hosting.Link;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>The pieces between the window and the session host: the line protocol, plugins' addresses, and kept output.</summary>
public sealed class SessionHostLinkTests
{
    /// <summary>Two channels joined by an anonymous pipe pair, each side's writes the other's reads.</summary>
    private static (LinkChannel A, LinkChannel B) Pair()
    {
        var name = "lt-" + Guid.NewGuid().ToString("N")[..12];   // short: macOS caps a socket path at 104 characters, and its temp folder is long
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync();
        client.Connect(5000);
        waiting.Wait(5000);
        return (new LinkChannel(server), new LinkChannel(client));
    }

    [Fact]
    public async Task Messages_arrive_whole_in_order_with_their_kind()
    {
        var (a, b) = Pair();
        var got = new List<(char, string)>();
        var all = new TaskCompletionSource();
        b.Received += (k, json) => { lock (got) { got.Add((k, json)); if (got.Count == 3) all.TrySetResult(); } };
        a.Start();
        b.Start();
        var big = "{\"data\":\"" + new string('x', 200_000) + "\"}";
        a.Send(LinkKind.Hello, """{"pid":1}""");
        a.Send(LinkKind.Page, big);
        a.Send(LinkKind.Page, "{\"type\":\"output\",\n\"data\":\"a\\nb ü\"}");   // a stray line break is only whitespace
        await all.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((LinkKind.Hello, """{"pid":1}"""), got[0]);
        Assert.Equal((LinkKind.Page, big), got[1]);
        Assert.Equal((LinkKind.Page, "{\"type\":\"output\", \"data\":\"a\\nb ü\"}"), got[2]);
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task Closing_one_side_tells_the_other()
    {
        var (a, b) = Pair();
        var closed = new TaskCompletionSource();
        b.Closed += () => closed.TrySetResult();
        a.Start();
        b.Start();
        await a.FlushAndCloseAsync(TimeSpan.FromSeconds(2));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(b.IsOpen);
    }

    [Fact]
    public async Task A_line_longer_than_allowed_ends_the_connection_and_says_why()
    {
        var huge = new string('x', LinkChannel.MaxLine + 10);
        var fromServer = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("P {\"a\":1}\nP " + huge + "\nP {\"b\":2}\n"));
        var link = new LinkChannel(new DuplexStream(fromServer, new MemoryStream(), () => { }));
        var got = new List<string>();
        var closed = new TaskCompletionSource();
        link.Received += (_, json) => { lock (got) got.Add(json); };
        link.Closed += () => closed.TrySetResult();
        link.Start();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["{\"a\":1}"], got);   // none of it, nor anything after it
        Assert.Contains("longer than", link.CloseReason);
    }

    [Fact]
    public async Task A_side_that_stops_reading_is_disconnected_rather_than_queued_for()
    {
        var (a, b) = Pair();   // b never reads
        var closed = new TaskCompletionSource();
        a.Closed += () => closed.TrySetResult();
        a.Start();
        var line = "{\"data\":\"" + new string('x', 1 << 20) + "\"}";
        for (var i = 0; i < LinkChannel.MaxOutbox / line.Length + 2 && a.IsOpen; i++) a.Send(LinkKind.Page, line);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("stopped reading", a.CloseReason);
        b.Dispose();
    }

    [Fact]
    public async Task Reading_pauses_while_too_much_received_is_still_being_handled()
    {
        var (a, b) = Pair();
        var holds = new List<Action>();
        var count = 0;
        b.Received += (_, json) => { lock (holds) { holds.Add(b.Hold(json.Length)); count++; } };   // handled later: not until let go
        a.Start();
        b.Start();
        var line = "{\"data\":\"" + new string('x', 1 << 20) + "\"}";
        var sent = LinkChannel.MaxHeld / line.Length + 8;
        for (var i = 0; i < sent; i++) a.Send(LinkKind.Page, line);
        await Task.Delay(1500);
        int paused;
        lock (holds) paused = count;
        Assert.True(paused <= LinkChannel.MaxHeld / line.Length + 1, $"read {paused} of {sent} while all were held");
        Assert.True(a.IsOpen && b.IsOpen);   // the sender waits; nobody is cut off
        // Once they're handled, the rest comes. Some tens of megabytes over a pipe: a slow CI machine (macOS on Intel) has
        // taken more than 10 seconds, so it's given 30; the loop ends as soon as they're all in.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            lock (holds)
            {
                foreach (var done in holds) done();
                holds.Clear();
                if (count == sent) break;
            }
            await Task.Delay(50);
        }
        Assert.Equal(sent, count);
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task A_handler_that_throws_fails_only_its_message()
    {
        var (a, b) = Pair();
        var got = new List<string>();
        var both = new TaskCompletionSource();
        b.Received += (_, json) =>
        {
            if (json.Contains("bad")) throw new InvalidOperationException("a bad message");
            lock (got) { got.Add(json); if (got.Count == 2) both.TrySetResult(); }
        };
        a.Start();
        b.Start();
        a.Send(LinkKind.Page, """{"n":1}""");
        a.Send(LinkKind.Page, """{"bad":true}""");
        a.Send(LinkKind.Page, """{"n":2}""");
        await both.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["""{"n":1}""", """{"n":2}"""], got);
        Assert.True(b.IsOpen);
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void A_plugin_keeps_its_address_for_the_run_and_others_cant_guess_it()
    {
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var site = new PluginAddressing("http://127.0.0.1:5000", key);
        Assert.Equal(site.Base("notes"), site.Base("notes"));
        Assert.NotEqual(site.Base("notes"), site.Base("board"));
        Assert.StartsWith("http://127.0.0.1:5000/plugin/", site.Base("notes"));
        var otherRun = new PluginAddressing("http://127.0.0.1:5000", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        Assert.NotEqual(site.Base("notes"), otherRun.Base("notes"));
        Assert.Equal("https://notes.plugin.local/", PluginAddressing.VirtualHosts.Base("notes"));
    }

    [Theory]
    [InlineData("dev@server", true)]
    [InlineData("server", true)]
    [InlineData("ssh://dev@server:2222", true)]
    [InlineData("dev@[fe80::1]", true)]
    [InlineData("", false)]
    [InlineData("-oProxyCommand=calc", false)]   // an option, not a server
    [InlineData("dev@server ls", false)]          // a command after it
    [InlineData("dev@server\nls", false)]
    public void A_server_is_only_ever_one_ssh_destination(string target, bool valid) =>
        Assert.Equal(valid, LlamaTree.Hosting.HostLink.IsValidTarget(target));

    [Fact]
    public async Task A_programs_output_and_input_make_one_connection()
    {
        // As the window uses ssh's: lines read from one stream, written to the other.
        var fromServer = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("W {\"op\":\"welcome\"}\n"));
        var toServer = new MemoryStream();
        var disposed = false;
        var link = new LinkChannel(new DuplexStream(fromServer, toServer, () => disposed = true));
        var got = new TaskCompletionSource<string>();
        var closed = new TaskCompletionSource();
        link.Received += (kind, json) => got.TrySetResult(kind + json);
        link.Closed += () => closed.TrySetResult();
        link.Start();
        link.Send(LinkKind.Hello, """{"remote":"dev@server"}""");
        Assert.Equal("W{\"op\":\"welcome\"}", await got.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the output ended: so does the connection
        Assert.True(disposed);
    }

    [Fact]
    public void Kept_output_is_the_end_of_it_cut_at_a_line_break()
    {
        var tail = new OutputTail(max: 100);
        tail.Append("first line\r\n");
        for (var i = 0; i < 30; i++) tail.Append($"line {i:00}\r\n");
        var kept = tail.ToString();
        Assert.True(kept.Length <= 100);
        Assert.EndsWith("line 29\r\n", kept);
        Assert.StartsWith("line ", kept);   // starts at a line, never part-way through one (or an escape sequence)
        tail.Clear();
        Assert.Equal("", tail.ToString());
    }
}
