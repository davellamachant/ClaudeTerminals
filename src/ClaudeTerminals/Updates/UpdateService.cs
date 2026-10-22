using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeTerminals.Updates;

/// <param name="Sha256">What the installer must hash to: GitHub's own digest of the file, or else the one in the notes.</param>
internal sealed record UpdateInfo(Version Version, string Name, string PageUrl, string Notes, string AssetName, string AssetUrl, long Size, string? Sha256);

/// <summary>
/// Updates from the GitHub releases of the public repository: finds the latest release, downloads its installer,
/// checks it against the SHA-256 GitHub publishes for the file, and hands over to a silent install that reopens the app.
/// Nothing is sent but the anonymous requests for the release and the file.
/// </summary>
internal static partial class UpdateService
{
    private const string Repo = "davellamachant/ClaudeTerminals";

    /// <summary>For testing against a local server; plain http is then allowed, but only to this machine.</summary>
    private static readonly string? TestUrl =
        Environment.GetEnvironmentVariable("CLAUDE_TERMINALS_UPDATE_URL") is { } url && !string.IsNullOrWhiteSpace(url) ? url : null;   // set but empty = not set

    private static string LatestUrl => TestUrl ?? $"https://api.github.com/repos/{Repo}/releases/latest";

    // Before Http: static initializers run in order, and the client's User-Agent carries the version.
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClaudeTerminals", CurrentVersion.ToString()));
        return http;
    }

    /// <summary>1.0.7 and 1.0.7.0 are the same version.</summary>
    internal static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>The latest release if it's newer than this copy, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no releases yet
        response.EnsureSuccessStatusCode();
        var info = Parse(await response.Content.ReadAsStringAsync(ct));
        return info != null && info.Version > CurrentVersion ? info : null;
    }

    [GeneratedRegex(@"SHA-256[\s\S]{0,300}?\b([0-9a-fA-F]{64})\b")]
    private static partial Regex NotesHash();

    /// <summary>Reads GitHub's "latest release" answer; null when it has no usable version or installer.</summary>
    internal static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name") ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        // The installer for this version; any other Setup .exe only if the name doesn't carry a version.
        var wanted = $"ClaudeTerminals-Setup-{version}.exe";
        JsonElement? asset = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = Str(a, "name") ?? "";
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) { asset = a; break; }
        }
        if (asset is not { } chosen) return null;

        var notes = Str(r, "body") ?? "";
        var digest = Str(chosen, "digest");
        var fromGitHub = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
        var fromNotes = NotesHash().Match(notes) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
        // Two different hashes means something is wrong with the release: don't trust either.
        var sha = fromGitHub != null && fromNotes != null && fromGitHub != fromNotes ? null : fromGitHub ?? fromNotes;

        return new UpdateInfo(
            version,
            Str(r, "name") ?? $"Claude Terminals {version}",
            Str(r, "html_url") ?? $"https://github.com/{Repo}/releases",
            notes,
            Str(chosen, "name")!,
            Str(chosen, "browser_download_url") ?? "",
            chosen.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
            sha);
    }

    /// <summary>Downloads the installer into a new temporary folder and checks it. Returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (info.Sha256 == null)
            throw new InvalidOperationException("This release doesn't publish a checksum for its installer, so it can't be installed automatically. Download it from the release page instead.");
        if (!AllowedUrl(info.AssetUrl))
            throw new InvalidOperationException("The installer's address isn't a GitHub download, so it won't be used.");

        var dir = Path.Combine(Path.GetTempPath(), "ClaudeTerminals-Update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.AssetName);
        using (var response = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, ct);
        }
        await using (var file = File.OpenRead(path))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
            if (hash != info.Sha256 || (info.Size > 0 && file.Length != info.Size))
            {
                file.Close();
                try { Directory.Delete(dir, true); } catch (IOException) { }
                throw new InvalidOperationException("The downloaded installer doesn't match the release's checksum, so it wasn't run. Try again later, or download it from the release page.");
            }
        }
        return path;
    }

    private static bool AllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (TestUrl != null && u.IsLoopback) return true;
        return u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs the installer once this process has exited (setup won't replace a running copy): silently, with its
    /// progress window, then reopening the app. A hidden PowerShell waits for the exit.
    /// </summary>
    public static void InstallAfterExit(string installer)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var script = $"Wait-Process -Id {Environment.ProcessId} -Timeout 120 -ErrorAction SilentlyContinue; " +
                     $"Start-Process -FilePath {Quote(installer)} -ArgumentList '/SILENT','/SUPPRESSMSGBOXES','/NORESTART','/RELAUNCH=1'";
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installer)!,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script })
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
