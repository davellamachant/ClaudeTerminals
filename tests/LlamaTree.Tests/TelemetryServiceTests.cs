using System.Net;
using LlamaTree.Updates;

namespace LlamaTree.Tests;

/// <summary>Counting users: off until you say yes, one request a day that says only three things, and no trace once you say no.</summary>
public sealed class TelemetryServiceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ct-count-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Today = new(2026, 11, 3);

    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    [Fact]
    public void Until_youve_answered_nothing_is_owed()
    {
        var state = TelemetryService.Load(folder);
        Assert.Null(state.Enabled);
        Assert.Null(state.Id);
        Assert.False(TelemetryService.IsDue(state, Today));
    }

    [Fact]
    public void Yes_makes_an_id_and_keeps_it()
    {
        var yes = TelemetryService.SetEnabled(true, folder);
        Assert.True(yes.Enabled);
        Assert.Matches("^[0-9a-f]{32}$", yes.Id);
        Assert.Equal(yes, TelemetryService.Load(folder));
        Assert.Equal(yes.Id, TelemetryService.SetEnabled(true, folder).Id);   // yes again changes nothing
        Assert.True(TelemetryService.IsDue(yes, Today));
    }

    [Fact]
    public void No_forgets_the_id_and_yes_again_starts_over()
    {
        var first = TelemetryService.SetEnabled(true, folder);
        var no = TelemetryService.SetEnabled(false, folder);
        Assert.False(no.Enabled);
        Assert.Null(no.Id);
        Assert.Null(no.LastSent);
        Assert.False(TelemetryService.IsDue(no, Today));
        Assert.Equal(no, TelemetryService.Load(folder));

        var again = TelemetryService.SetEnabled(true, folder);
        Assert.NotEqual(first.Id, again.Id);
    }

    [Fact]
    public void One_request_a_day()
    {
        var state = TelemetryService.SetEnabled(true, folder);
        Assert.True(TelemetryService.IsDue(state, Today));
        Assert.False(TelemetryService.IsDue(state with { LastSent = Today }, Today));
        Assert.True(TelemetryService.IsDue(state with { LastSent = Today.AddDays(-1) }, Today));
    }

    [Fact]
    public void The_request_says_the_id_the_version_and_the_Windows_version_and_nothing_else()
    {
        var uri = TelemetryService.PingUri(TelemetryService.PingUrl, "0123456789abcdef0123456789abcdef", new Version(1, 2, 3), new Version(10, 0, 26200, 1));
        Assert.Equal("https://downloads.llamachant.com/downloads/TerminalsData/ping?id=0123456789abcdef0123456789abcdef&v=1.2.3&os=10.0.26200", uri.ToString());
    }

    [Theory]
    [InlineData("https://downloads.llamachant.com/downloads/TerminalsData/ping", true)]
    [InlineData("http://downloads.llamachant.com/downloads/TerminalsData/ping", false)]   // not over https
    [InlineData("https://example.com/ping", false)]   // not our site
    [InlineData("http://127.0.0.1:5/ping", false)]   // this machine, but only while testing (LLAMATREE_TELEMETRY_URL)
    public void Only_our_site_is_asked(string url, bool allowed) => Assert.Equal(allowed, TelemetryService.AllowedUrl(url));

    [Fact]
    public void A_file_edited_by_hand_cant_be_on_without_an_id()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, TelemetryService.FileName), """{ "enabled": true, "id": "not-an-id" }""");
        var state = TelemetryService.Load(folder);
        Assert.False(state.Enabled);
        Assert.Null(state.Id);
        Assert.False(TelemetryService.IsDue(state, Today));
    }

    [Fact]
    public void A_corrupt_file_asks_again()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, TelemetryService.FileName), "{ not json");
        Assert.Null(TelemetryService.Load(folder).Enabled);
    }

    [Fact]
    public async Task Nothing_is_sent_until_you_say_yes_and_once_sent_the_day_is_done()
    {
        using var server = new PingServer();

        Assert.False(await TelemetryService.PingIfDueAsync(folder, server.Url, Today));   // not asked yet
        TelemetryService.SetEnabled(false, folder);
        Assert.False(await TelemetryService.PingIfDueAsync(folder, server.Url, Today));   // said no
        Assert.Empty(server.Requests);

        var yes = TelemetryService.SetEnabled(true, folder);
        Assert.True(await TelemetryService.PingIfDueAsync(folder, server.Url, Today));
        var request = Assert.Single(server.Requests);
        Assert.Equal($"id={yes.Id}&v={UpdateService.CurrentVersion}&os={Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{Environment.OSVersion.Version.Build}", request.Query.TrimStart('?'));
        Assert.StartsWith("LlamaTree/", request.UserAgent);
        Assert.Equal(Today, TelemetryService.Load(folder).LastSent);

        Assert.False(await TelemetryService.PingIfDueAsync(folder, server.Url, Today));   // today's is done
        Assert.Single(server.Requests);
        Assert.True(await TelemetryService.PingIfDueAsync(folder, server.Url, Today.AddDays(1)));   // tomorrow's isn't
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Any_answer_counts_even_a_404()
    {
        using var server = new PingServer(HttpStatusCode.NotFound);
        TelemetryService.SetEnabled(true, folder);
        Assert.True(await TelemetryService.PingIfDueAsync(folder, server.Url, Today));
        Assert.Equal(Today, TelemetryService.Load(folder).LastSent);
    }

    [Fact]
    public async Task Offline_it_stays_owed()
    {
        TelemetryService.SetEnabled(true, folder);
        // A loopback port nobody listens on; the test URL rule lets it through, the connection is refused.
        Assert.False(await TelemetryService.PingIfDueAsync(folder, "http://127.0.0.1:1/ping", Today));
        Assert.Null(TelemetryService.Load(folder).LastSent);
    }

    /// <summary>A local site that logs what it's asked, in place of the download site.</summary>
    private sealed class PingServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly HttpStatusCode status;
        public readonly List<(string Query, string? UserAgent)> Requests = [];
        public string Url { get; }

        public PingServer(HttpStatusCode status = HttpStatusCode.OK)
        {
            this.status = status;
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/ping";
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { return; }   // stopped
                lock (Requests) Requests.Add((ctx.Request.Url?.Query ?? "", ctx.Request.UserAgent));
                ctx.Response.StatusCode = (int)status;
                ctx.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        public void Dispose() => listener.Stop();
    }
}
