using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaTree.Updates;

/// <param name="Sha256">What the installer must hash to: GitHub's own digest of the file, or else the one in the notes.</param>
/// <param name="ForThisMachine">Whether the asset is the one this copy installs itself from (on macOS, or a Linux release
/// without its packages, it is the Windows installer, there only to say the release exists).</param>
/// <param name="ChecksumsUrl">The release's SHA256SUMS, and <paramref name="ChecksumsSignatureUrl"/> our signature of it
/// (SHA256SUMS.p7s); null when the release doesn't carry them.</param>
internal sealed record UpdateInfo(Version Version, string Name, string PageUrl, string Notes, string AssetName, string AssetUrl, long Size, string? Sha256,
    bool ForThisMachine = true, string? ChecksumsUrl = null, string? ChecksumsSignatureUrl = null)
{
    public bool CanInstall => Sha256 != null && ForThisMachine && UpdateService.CanInstallHere;
}

/// <summary>
/// Updates from the GitHub releases of the public repository: finds the latest release, downloads its installer,
/// checks it, and hands over to a silent install that reopens the app. Nothing is sent but the anonymous requests for
/// the release and the files.
/// <para>
/// What's on GitHub isn't trusted on its own: whoever got hold of a token that can publish there could replace both
/// a file and the checksum GitHub shows for it. So a release also carries SHA256SUMS, signed (SHA256SUMS.p7s) on the
/// build agent with the company's code signing certificate, which never leaves its hardware token; the download must
/// match the checksum in it, and the signature must be <see cref="Publisher">ours</see>. On Windows the installer's own
/// Authenticode signature must be ours as well. Anything less and nothing is installed.
/// </para>
/// </summary>
internal static partial class UpdateService
{
    private const string Repo = "LlamachantTechnology/LlamaTree";

    /// <summary>The signed list of a release's checksums, and its signature (see tools/sign-checksums.cs).</summary>
    internal const string ChecksumsName = "SHA256SUMS";
    internal const string ChecksumsSignatureName = ChecksumsName + ".p7s";

    /// <summary>
    /// The first release that carries signed checksums; every one from it on must. 1.3.0 and earlier don't, but this
    /// copy only ever updates to a newer release than itself, and all of those are newer than 1.3.0.
    /// </summary>
    internal static readonly Version SignedSince = new(1, 3, 1);

    /// <summary>For testing against a local server; plain http is then allowed, but only to this machine.</summary>
    private static readonly string? TestUrl =
        AppProfile.Variable("LLAMATREE_UPDATE_URL", "CLAUDE_TERMINALS_UPDATE_URL") is { } url && !string.IsNullOrWhiteSpace(url) ? url : null;   // set but empty = not set

    /// <summary>Whether a local test server stands in for GitHub (LLAMATREE_UPDATE_URL).</summary>
    public static bool Testing => TestUrl != null;

    private static string LatestUrl => TestUrl ?? $"https://api.github.com/repos/{Repo}/releases/latest";

    // Before Http: static initializers run in order, and the client's User-Agent carries the version.
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    /// <summary>How this copy was installed: decides which release asset it updates from, and how.</summary>
    public static InstallKind Install => install.Value;

    private static readonly Lazy<InstallKind> install = new(() =>
        OperatingSystem.IsWindows() ? InstallKind.Windows : OperatingSystem.IsLinux() ? LinuxUpdate.Detect() : InstallKind.None);

    /// <summary>Whether an update can install itself; otherwise (macOS for now) the dialog links the release page.</summary>
    public static bool CanInstallHere => Install != InstallKind.None;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LlamaTree", CurrentVersion.ToString()));
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

    /// <summary>
    /// The files of the release for <paramref name="version"/> (a server is updated to match this computer, see
    /// ServerUpdate), each with its SHA-256: name → (download address, hash). From <see cref="SignedSince"/> on, the
    /// hashes are those in the release's signed SHA256SUMS, and only files listed there (with GitHub's digest, where it
    /// gives one, agreeing) are listed; before, GitHub's own digests. Throws when there's no such release, or its
    /// checksums aren't signed by us.
    /// </summary>
    public static async Task<Dictionary<string, (string Url, string Sha256)>> ReleaseFilesAsync(Version version, CancellationToken ct = default)
    {
        var url = TestUrl ?? $"https://api.github.com/repos/{Repo}/releases/tags/v{version}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"There's no LlamaTree {version} release on GitHub to update to.");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var gitHub = new Dictionary<string, (string Url, string? Sha256)>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
                if (Str(a, "name") is { } name && Str(a, "browser_download_url") is { } link)
                    gitHub[name] = (link, Str(a, "digest") is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest[7..].ToLowerInvariant() : null);

        var files = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (Normalize(version) < SignedSince)
        {
            foreach (var (name, (link, sha)) in gitHub)
                if (sha != null) files[name] = (link, sha);
            return files;
        }
        var signed = await SignedChecksumsAsync(gitHub.GetValueOrDefault(ChecksumsName).Url, gitHub.GetValueOrDefault(ChecksumsSignatureName).Url, ct);
        foreach (var (name, (link, sha)) in gitHub)
            if (signed.TryGetValue(name, out var hash) && (sha == null || sha == hash)) files[name] = (link, hash);
        return files;
    }

    /// <summary>
    /// The checksums in a release's SHA256SUMS (file name → lowercase hex SHA-256) once its signature, SHA256SUMS.p7s,
    /// is found to be ours. Throws, saying so, when either is missing or the signature isn't ours.
    /// </summary>
    private static async Task<Dictionary<string, string>> SignedChecksumsAsync(string? url, string? signatureUrl, CancellationToken ct)
    {
        if (url == null || signatureUrl == null)
            throw new InvalidOperationException("This release doesn't carry the signed list of its checksums, so it wasn't installed. Download it from the release page instead.");
        if (!AllowedUrl(url) || !AllowedUrl(signatureUrl))
            throw new InvalidOperationException("The release's checksums aren't a GitHub download, so they won't be used.");
        var content = Http.GetByteArrayAsync(url, ct);
        var signature = Http.GetByteArrayAsync(signatureUrl, ct);
        return VerifiedChecksums(await content, await signature)
            ?? throw new InvalidOperationException($"The release's list of checksums isn't signed by {Publisher.Name}, so nothing was installed. Download it from the release page instead.");
    }

    /// <summary>The checksums in <paramref name="content"/> (SHA256SUMS) if <paramref name="signature"/> is ours over it; otherwise null.</summary>
    internal static Dictionary<string, string>? VerifiedChecksums(byte[] content, byte[] signature, System.Security.Cryptography.X509Certificates.X509Certificate2Collection? roots = null) =>
        content.Length <= 1 << 20 && Publisher.VerifyDetached(content, signature, roots) ? ParseChecksums(System.Text.Encoding.UTF8.GetString(content)) : null;

    [GeneratedRegex(@"^([0-9a-fA-F]{64}) [ *]([^\r\n/\\]+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex ChecksumLine();

    /// <summary>sha256sum's format, "&lt;hash&gt;  &lt;name&gt;" a line (" *" for binary mode): name → lowercase hash. A name listed twice counts for neither.</summary>
    internal static Dictionary<string, string> ParseChecksums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var twice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ChecksumLine().Matches(text))
            if (!sums.TryAdd(m.Groups[2].Value, m.Groups[1].Value.ToLowerInvariant())) twice.Add(m.Groups[2].Value);
        foreach (var name in twice) sums.Remove(name);
        return sums;
    }

    [GeneratedRegex(@"SHA-256[\s\S]{0,300}?\b([0-9a-fA-F]{64})\b")]
    private static partial Regex NotesHash();

    /// <summary>Reads GitHub's "latest release" answer; null when it has no usable version or installer.</summary>
    internal static UpdateInfo? Parse(string json) => Parse(json, Install);

    internal static UpdateInfo? Parse(string json, InstallKind kind)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name") ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        // The installer for this version. Releases also carry it under its name from before the rename, for copies that
        // only know that one, so it is the fallback. On Linux, the package for how this copy was installed comes first;
        // the Windows installer still shows the release exists when that package is missing (or on macOS).
        string[] ours = kind == InstallKind.Windows ? [] : LinuxUpdate.AssetName(kind, version) is { } linux ? [linux] : [];
        string[] wanted = [.. ours, $"LlamaTree-Setup-{version}.exe", $"ClaudeTerminals-Setup-{version}.exe"];
        JsonElement? asset = null;
        var matched = -1;
        for (var i = 0; i < wanted.Length && asset == null; i++)
        {
            foreach (var a in assets.EnumerateArray())
                if (string.Equals(Str(a, "name"), wanted[i], StringComparison.OrdinalIgnoreCase)) { asset = a; matched = i; break; }
        }
        if (asset is not { } chosen) return null;
        var forThisMachine = kind == InstallKind.Windows || matched < ours.Length;

        var notes = Str(r, "body") ?? "";
        var digest = Str(chosen, "digest");
        var fromGitHub = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
        var fromNotes = NotesHash().Match(notes) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
        // Two different hashes means something is wrong with the release: don't trust either.
        var sha = fromGitHub != null && fromNotes != null && fromGitHub != fromNotes ? null : fromGitHub ?? fromNotes;

        string? Link(string name) => assets.EnumerateArray()
            .Where(a => string.Equals(Str(a, "name"), name, StringComparison.OrdinalIgnoreCase)).Select(a => Str(a, "browser_download_url")).FirstOrDefault();

        return new UpdateInfo(
            version,
            Str(r, "name") ?? $"LlamaTree {version}",
            Str(r, "html_url") ?? $"https://github.com/{Repo}/releases",
            notes,
            Str(chosen, "name")!,
            Str(chosen, "browser_download_url") ?? "",
            chosen.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
            sha,
            forThisMachine,
            Link(ChecksumsName),
            Link(ChecksumsSignatureName));
    }

    /// <summary>
    /// Downloads the installer into a new temporary folder and checks it (see <see cref="Refusal"/>). Returns its path;
    /// throws, saying why, when it isn't to be installed, having deleted it.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (info.Sha256 == null)
            throw new InvalidOperationException("This release doesn't publish a checksum for its installer, so it can't be installed automatically. Download it from the release page instead.");
        if (!AllowedUrl(info.AssetUrl))
            throw new InvalidOperationException("The installer's address isn't a GitHub download, so it won't be used.");
        // The signed checksums first: without them there's no point downloading the installer.
        var signed = info.Version < SignedSince ? null : await SignedChecksumsAsync(info.ChecksumsUrl, info.ChecksumsSignatureUrl, ct);

        var dir = Path.Combine(Path.GetTempPath(), "LlamaTree-Update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.AssetName);
        string? refusal;
        try
        {
            using (var response = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(path);
                await response.Content.CopyToAsync(file, ct);
            }
            refusal = Refusal(info, path, signed);
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            throw;
        }
        if (refusal == null) return path;
        try { Directory.Delete(dir, true); } catch (IOException) { }
        throw new InvalidOperationException(refusal);
    }

    /// <summary>
    /// Why the downloaded <paramref name="path"/> mustn't be installed, or null when it may: it must match the release's
    /// checksum and size, and the checksum in our signed list (<paramref name="signed"/>; null only for a release from
    /// before <see cref="SignedSince"/>). A Windows installer must also carry our Authenticode signature
    /// (<paramref name="signedByUs"/> stands in for that check in tests).
    /// </summary>
    internal static string? Refusal(UpdateInfo info, string path, IReadOnlyDictionary<string, string>? signed, Func<string, bool>? signedByUs = null)
    {
        string hash;
        long length;
        using (var file = File.OpenRead(path))
        {
            hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            length = file.Length;
        }
        if (hash != info.Sha256 || (info.Size > 0 && length != info.Size))
            return "The downloaded installer doesn't match the release's checksum, so it wasn't run. Try again later, or download it from the release page.";
        if (signed == null && info.Version >= SignedSince)
            return "This release doesn't carry the signed list of its checksums, so it wasn't installed. Download it from the release page instead.";
        if (signed != null && (!signed.TryGetValue(info.AssetName, out var ours) || ours != hash))
            return $"The downloaded installer isn't the one {Publisher.Name} signed for this release, so it wasn't run. Download it from the release page instead.";
        if (info.AssetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !(signedByUs ?? (p => OperatingSystem.IsWindows() && Publisher.IsSignedByUs(p)))(path))
            return $"The downloaded installer isn't signed by {Publisher.Name}, so it wasn't run. Download it from the release page instead.";
        return null;
    }

    private static bool AllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (TestUrl != null && u.IsLoopback) return true;
        return u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs the installer once <paramref name="processes"/> (the session host and its window) have exited, as setup won't
    /// replace a running copy: silently, with its progress window, then reopening the app. A hidden PowerShell waits.
    /// </summary>
    public static void InstallAfterExit(string installer, int[] processes)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var script = $"Wait-Process -Id {string.Join(',', processes)} -Timeout 120 -ErrorAction SilentlyContinue; " +
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
