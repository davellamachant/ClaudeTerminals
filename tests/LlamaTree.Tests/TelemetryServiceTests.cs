using System.Net;
using LlamaTree.Updates;

namespace LlamaTree.Tests;

/// <summary>Counting users: off until you say yes, one request an hour that says only six things, and no trace once you say no.</summary>
public sealed class TelemetryServiceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ct-count-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 3, 14, 25, 0, DateTimeKind.Utc);
    private static readonly CountUsage Usage = new(Projects: 3, Sessions: 7);

    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    [Fact]
    public void Until_youve_answered_nothing_is_owed()
    {
        var state = TelemetryService.Load(folder);
        Assert.Null(state.Enabled);
        Assert.Null(state.Id);
        Assert.False(TelemetryService.IsDue(state, Now));
    }

    [Fact]
    public void Yes_makes_an_id_and_keeps_it()
    {
        var yes = TelemetryService.SetEnabled(true, folder);
        Assert.True(yes.Enabled);
        Assert.Matches("^[0-9a-f]{32}$", yes.Id);
        Assert.Equal(yes, TelemetryService.Load(folder));
        Assert.Equal(yes.Id, TelemetryService.SetEnabled(true, folder).Id);   // yes again changes nothing
        Assert.True(TelemetryService.IsDue(yes, Now));
    }

    [Fact]
    public void No_forgets_the_id_and_yes_again_starts_over()
    {
        var first = TelemetryService.SetEnabled(true, folder);
        var no = TelemetryService.SetEnabled(false, folder);
        Assert.False(no.Enabled);
        Assert.Null(no.Id);
        Assert.Null(no.LastSent);
        Assert.False(TelemetryService.IsDue(no, Now));
        Assert.Equal(no, TelemetryService.Load(folder));
        Assert.DoesNotContain(first.Id!, File.ReadAllText(Path.Combine(folder, TelemetryService.FileName)));   // gone from disk, not just ignored

        var again = TelemetryService.SetEnabled(true, folder);
        Assert.NotEqual(first.Id, again.Id);
    }

    [Fact]
    public void One_request_an_hour()
    {
        var state = TelemetryService.SetEnabled(true, folder);
        var hour = TelemetryService.HourOf(Now);
        Assert.Equal(new DateTime(2026, 11, 3, 14, 0, 0, DateTimeKind.Utc), hour);
        Assert.True(TelemetryService.IsDue(state, Now));
        Assert.False(TelemetryService.IsDue(state with { LastSent = hour }, Now));
        Assert.False(TelemetryService.IsDue(state with { LastSent = hour }, Now.AddMinutes(34)));   // 14:59, the same hour
        Assert.True(TelemetryService.IsDue(state with { LastSent = hour }, Now.AddMinutes(35)));    // 15:00, the next
        Assert.True(TelemetryService.IsDue(state with { LastSent = hour.AddDays(-1) }, Now));
    }

    [Fact]
    public void The_hour_is_kept_in_the_file()
    {
        var state = TelemetryService.SetEnabled(true, folder);
        TelemetryService.Save(state with { LastSent = TelemetryService.HourOf(Now) }, folder);
        Assert.Contains("\"lastSent\": \"2026-11-03T14\"", File.ReadAllText(Path.Combine(folder, TelemetryService.FileName)));
        Assert.Equal(TelemetryService.HourOf(Now), TelemetryService.Load(folder).LastSent);
    }

    [Fact]
    public void The_request_says_the_id_the_versions_the_system_and_the_counts_and_nothing_else()
    {
        var uri = TelemetryService.PingUri(TelemetryService.PingUrl, "0123456789abcdef0123456789abcdef", new Version(1, 2, 3), "windows", new Version(10, 0, 26200, 1), Usage);
        Assert.Equal("https://api.llamatree.dev/count/ping?id=0123456789abcdef0123456789abcdef&v=1.2.3&os=windows&osv=10.0.26200&projects=3&sessions=7", uri.ToString());
    }

    [Fact]
    public void The_system_is_named_for_the_platform_it_runs_on() =>
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux", TelemetryService.OsName());

    [Theory]
    [InlineData("https://api.llamatree.dev/count/ping", true)]
    [InlineData("http://api.llamatree.dev/count/ping", false)]   // not over https
    [InlineData("https://downloads.llamachant.com/ping", false)]   // ours, but not the count's
    [InlineData("https://example.com/ping", false)]   // not our site
    [InlineData("http://127.0.0.1:5/ping", false)]   // this machine, but only while testing (LLAMATREE_TELEMETRY_URL)
    public void Only_our_count_service_is_asked(string url, bool allowed) => Assert.Equal(allowed, TelemetryService.AllowedUrl(url));

    [Fact]
    public void A_file_edited_by_hand_cant_be_on_without_an_id()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, TelemetryService.FileName), """{ "enabled": true, "id": "not-an-id" }""");
        var state = TelemetryService.Load(folder);
        Assert.False(state.Enabled);
        Assert.Null(state.Id);
        Assert.False(TelemetryService.IsDue(state, Now));
    }

    [Fact]
    public void A_corrupt_file_asks_again()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, TelemetryService.FileName), "{ not json");
        Assert.Null(TelemetryService.Load(folder).Enabled);
    }

    [Fact]
    public async Task Nothing_is_sent_until_you_say_yes_and_once_sent_the_hour_is_done()
    {
        using var server = new PingServer();

        Assert.False(await TelemetryService.PingIfDueAsync(Usage, folder, server.Url, Now));   // not asked yet
        TelemetryService.SetEnabled(false, folder);
        Assert.False(await TelemetryService.PingIfDueAsync(Usage, folder, server.Url, Now));   // said no
        Assert.Empty(server.Requests);

        var yes = TelemetryService.SetEnabled(true, folder);
        Assert.True(await TelemetryService.PingIfDueAsync(Usage, folder, server.Url, Now));
        var request = Assert.Single(server.Requests);
        var os = Environment.OSVersion.Version;
        Assert.Equal($"id={yes.Id}&v={UpdateService.CurrentVersion}&os={TelemetryService.OsName()}&osv={os.Major}.{Math.Max(os.Minor, 0)}.{Math.Max(os.Build, 0)}&projects=3&sessions=7", request.Query.TrimStart('?'));
        Assert.StartsWith("LlamaTree/", request.UserAgent);
        Assert.Equal(TelemetryService.HourOf(Now), TelemetryService.Load(folder).LastSent);

        Assert.False(await TelemetryService.PingIfDueAsync(Usage, folder, server.Url, Now.AddMinutes(30)));   // this hour's is done
        Assert.Single(server.Requests);
        Assert.True(await TelemetryService.PingIfDueAsync(Usage, folder, server.Url, Now.AddHours(1)));   // the next hour's isn't
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Any_answer_counts_even_a_404()
    {
        using var server = new PingServer(HttpStatusCode.NotFound);
        TelemetryService.SetEnabled(true, folder);
        Assert.True(await TelemetryService.PingIfDueAsync(Usage, folder, server.Url, Now));
        Assert.Equal(TelemetryService.HourOf(Now), TelemetryService.Load(folder).LastSent);
    }

    [Fact]
    public async Task Offline_it_stays_owed()
    {
        TelemetryService.SetEnabled(true, folder);
        // A loopback port nobody listens on; the test URL rule lets it through, the connection is refused.
        Assert.False(await TelemetryService.PingIfDueAsync(Usage, folder, "http://127.0.0.1:1/ping", Now));
        Assert.Null(TelemetryService.Load(folder).LastSent);
    }

    /// <summary>A local site that logs what it's asked, in place of the count service.</summary>
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
