using System.Net.Http.Headers;
using System.Text.Json;
using LlamaTree.Sessions;

namespace LlamaTree.Updates;

/// <summary>
/// What's remembered about the count: whether you've answered (null until you have), the id made when you said yes,
/// and the day (UTC) of the last request, so there's one a day.
/// </summary>
internal sealed record CountState(bool? Enabled, string? Id, DateOnly? LastSent);

/// <summary>
/// Counting users, only when you've said yes. Once a day the app then makes one request to our download site that
/// says three things: a random id made when you said yes (so a copy run every day counts once, not thirty times),
/// the app's version and the Windows version. The site's log is the count; the request is the whole of it, and any
/// answer, or none, will do. Nothing about you, your sessions, your projects or what's in them is sent, and saying no
/// throws the id away. The answer is kept beside the sessions in <c>telemetry.json</c>.
/// </summary>
internal static class TelemetryService
{
    /// <summary>Only ever this address, on our own download site: nothing on the machine can point the app elsewhere.</summary>
    internal const string PingUrl = "https://downloads.llamachant.com/downloads/TerminalsData/ping";

    internal const string FileName = "telemetry.json";

    /// <summary>For testing against a local server; plain http is then allowed, but only to this machine.</summary>
    private static readonly string? TestUrl =
        Environment.GetEnvironmentVariable("LLAMATREE_TELEMETRY_URL") is { } url && !string.IsNullOrWhiteSpace(url) ? url : null;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // No redirects: the request goes to our address or nowhere.
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LlamaTree", UpdateService.CurrentVersion.ToString()));
        return http;
    }

    private static string FilePath(string? folder) => Path.Combine(folder ?? SessionStore.AppDataFolder, FileName);

    /// <summary>The answer so far; not asked yet (and so off) when there's no file or it can't be read.</summary>
    public static CountState Load(string? folder = null)
    {
        try
        {
            if (!File.Exists(FilePath(folder))) return new CountState(null, null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath(folder)));
            var r = doc.RootElement;
            bool? enabled = r.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
            var id = r.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && IsId(i.GetString()) ? i.GetString() : null;
            DateOnly? last = r.TryGetProperty("lastSent", out var l) && l.ValueKind == JsonValueKind.String &&
                DateOnly.TryParseExact(l.GetString(), "yyyy-MM-dd", out var d) ? d : null;
            // Off without an id, on only with one: a file edited by hand can't make a half state.
            return enabled == true && id != null ? new CountState(true, id, last) : new CountState(enabled == null ? null : false, null, null);
        }
        catch (Exception)
        {
            return new CountState(null, null, null);   // a corrupt file asks again rather than stopping the app
        }
    }

    public static void Save(CountState state, string? folder = null)
    {
        var dir = folder ?? SessionStore.AppDataFolder;
        Directory.CreateDirectory(dir);
        var path = FilePath(folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { enabled = state.Enabled, id = state.Id, lastSent = state.LastSent?.ToString("yyyy-MM-dd") }, Options));
        File.Move(temp, path, true);
    }

    /// <summary>
    /// Your answer. Yes makes an id if there isn't one (a fresh one each time you come back to yes); no forgets the id
    /// and the day, so saying yes again starts over as a new user.
    /// </summary>
    public static CountState SetEnabled(bool on, string? folder = null)
    {
        var state = on
            ? Load(folder) is { Enabled: true, Id: { } id } current ? current : new CountState(true, Guid.NewGuid().ToString("N"), null)
            : new CountState(false, null, null);
        Save(state, folder);
        return state;
    }

    /// <summary>Whether a request is owed today: you've said yes and none has been made today.</summary>
    internal static bool IsDue(CountState state, DateOnly today) => state is { Enabled: true, Id: not null } && state.LastSent != today;

    /// <summary>The request's address: the id, the app's version and the Windows version, and nothing else.</summary>
    internal static Uri PingUri(string url, string id, Version version, Version os) =>
        new($"{url}?id={Uri.EscapeDataString(id)}&v={Uri.EscapeDataString(version.ToString())}&os={os.Major}.{Math.Max(os.Minor, 0)}.{Math.Max(os.Build, 0)}");

    /// <summary>Our site over https, or this machine when testing (the environment's test address, or a test's own).</summary>
    internal static bool AllowedUrl(string url, bool testing = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if ((testing || TestUrl != null) && u.IsLoopback) return true;
        return u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, "downloads.llamachant.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// Makes today's request if one is owed, and remembers the day when it's been made: true when it was. Offline, it
    /// stays owed and is tried again later. Any reply counts, even a 404: the site logged the request, which is all
    /// the count needs. <paramref name="url"/> and <paramref name="today"/> are for tests.
    /// </summary>
    public static async Task<bool> PingIfDueAsync(string? folder = null, string? url = null, DateOnly? today = null, CancellationToken ct = default)
    {
        var state = Load(folder);
        var day = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (!IsDue(state, day)) return false;
        var target = url ?? TestUrl ?? PingUrl;
        if (!AllowedUrl(target, testing: url != null)) return false;
        try
        {
            using var response = await Http.GetAsync(PingUri(target, state.Id!, UpdateService.CurrentVersion, Environment.OSVersion.Version), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
        // Still the same yes (you may have said no while it was on its way): then today is done.
        if (Load(folder) is { Enabled: true } now && now.Id == state.Id) Save(now with { LastSent = day }, folder);
        return true;
    }
}
