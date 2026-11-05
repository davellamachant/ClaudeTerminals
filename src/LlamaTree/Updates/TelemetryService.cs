using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using LlamaTree.Sessions;

namespace LlamaTree.Updates;

/// <summary>
/// What's remembered about the count: whether you've answered (null until you have), the id made when you said yes,
/// and the hour (UTC) of the last request, so there's one an hour.
/// </summary>
internal sealed record CountState(bool? Enabled, string? Id, DateTime? LastSent);

/// <summary>How much the app is being used when a request goes: the projects and the sessions open in it.</summary>
internal sealed record CountUsage(int Projects, int Sessions);

/// <summary>
/// Counting users, only when you've said yes. Once an hour while it's open, the app then makes one request to our
/// count service that says six things: a random id made when you said yes (so a copy counts once, however often it
/// asks), the app's version, the operating system's name and version, and how many projects and sessions are open.
/// Nothing about who you are, or what your sessions and projects are called or hold, is sent, and saying no throws the
/// id away. The answer is kept beside the sessions in <c>telemetry.json</c>.
/// </summary>
internal static class TelemetryService
{
    /// <summary>Only ever this address, our count service: nothing on the machine can point the app elsewhere.</summary>
    internal const string PingUrl = "https://api.llamatree.dev/count/ping";

    private const string PingHost = "api.llamatree.dev";

    internal const string FileName = "telemetry.json";

    private const string HourFormat = "yyyy-MM-dd'T'HH";

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

    /// <summary>The hour (UTC) a time falls in: the unit the count is made in.</summary>
    internal static DateTime HourOf(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

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
            DateTime? last = r.TryGetProperty("lastSent", out var l) && l.ValueKind == JsonValueKind.String &&
                DateTime.TryParseExact(l.GetString(), HourFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var h) ? h : null;
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
        File.WriteAllText(temp, JsonSerializer.Serialize(new
        {
            enabled = state.Enabled,
            id = state.Id,
            lastSent = state.LastSent?.ToString(HourFormat, CultureInfo.InvariantCulture),
        }, Options));
        File.Move(temp, path, true);
    }

    /// <summary>
    /// Your answer. Yes makes an id if there isn't one (a fresh one each time you come back to yes); no forgets the id
    /// and the hour, so saying yes again starts over as a new user.
    /// </summary>
    public static CountState SetEnabled(bool on, string? folder = null)
    {
        var state = on
            ? Load(folder) is { Enabled: true, Id: not null } current ? current : new CountState(true, Guid.NewGuid().ToString("N"), null)
            : new CountState(false, null, null);
        Save(state, folder);
        return state;
    }

    /// <summary>Whether a request is owed this hour: you've said yes and none has been made in it.</summary>
    internal static bool IsDue(CountState state, DateTime hour) => state is { Enabled: true, Id: not null } && state.LastSent != HourOf(hour);

    /// <summary>The operating system's name as the count gives it: windows, macos or linux (other platforms are to come).</summary>
    internal static string OsName() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other";

    /// <summary>The request's address: the id, the app's version, the system's name and version, and the counts. Nothing else.</summary>
    internal static Uri PingUri(string url, string id, Version version, string os, Version osVersion, CountUsage usage) =>
        new($"{url}?id={Uri.EscapeDataString(id)}&v={Uri.EscapeDataString(version.ToString())}" +
            $"&os={Uri.EscapeDataString(os)}&osv={osVersion.Major}.{Math.Max(osVersion.Minor, 0)}.{Math.Max(osVersion.Build, 0)}" +
            $"&projects={Math.Max(usage.Projects, 0)}&sessions={Math.Max(usage.Sessions, 0)}");

    /// <summary>Our count service over https, or this machine when testing (the environment's test address, or a test's own).</summary>
    internal static bool AllowedUrl(string url, bool testing = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if ((testing || TestUrl != null) && u.IsLoopback) return true;
        return u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, PingHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// Makes this hour's request if one is owed, and remembers the hour when it's been made: true when it was. Offline,
    /// it stays owed and is tried again later. Any reply counts, even an error: the request reached us.
    /// <paramref name="url"/> and <paramref name="now"/> are for tests.
    /// </summary>
    public static async Task<bool> PingIfDueAsync(CountUsage usage, string? folder = null, string? url = null, DateTime? now = null, CancellationToken ct = default)
    {
        var state = Load(folder);
        var hour = HourOf(now ?? DateTime.UtcNow);
        if (!IsDue(state, hour)) return false;
        var target = url ?? TestUrl ?? PingUrl;
        if (!AllowedUrl(target, testing: url != null)) return false;
        try
        {
            using var response = await Http.GetAsync(PingUri(target, state.Id!, UpdateService.CurrentVersion, OsName(), Environment.OSVersion.Version, usage), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
        // Still the same yes (you may have said no while it was on its way): then this hour is done.
        if (Load(folder) is { Enabled: true } current && current.Id == state.Id) Save(current with { LastSent = hour }, folder);
        return true;
    }
}
