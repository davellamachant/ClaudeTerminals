using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeTerminals.Updates;

/// <param name="Kind">tip, offer (something from someone else, such as a free subscription) or news.</param>
/// <param name="LinkUrl">Always https.</param>
/// <param name="Color">One of <see cref="Colors"/> (accent unless the notice says otherwise), or a #rrggbb of its own.</param>
internal sealed record Notice(string Id, string Kind, string Text, string? LinkLabel, string? LinkUrl, string Color = "accent");

/// <summary>
/// The suggestions shown along the top of the window: tips, offers and news, read from a <c>notices.json</c> on our own
/// site so they can change without a release. Each can be limited to dates and to versions of the app.
/// Nothing is sent but the anonymous request for the file.
/// </summary>
internal static partial class NoticeService
{
    /// <summary>
    /// Only ever this file, in our public storage account: nothing on the machine can point the app elsewhere. The
    /// account is built into the app by the release pipeline (NoticesStorageAccount); without it there are no notices.
    /// </summary>
    internal static readonly string? FeedUrl = FeedFor(Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "NoticesStorageAccount")?.Value);

    [GeneratedRegex("^[a-z0-9]{3,24}$")]
    private static partial Regex StorageAccountName();

    internal static string? FeedFor(string? storageAccount) =>
        storageAccount != null && StorageAccountName().IsMatch(storageAccount)
            ? $"https://{storageAccount}.blob.core.windows.net/downloads/TerminalsData/notices.json"
            : null;

    private const int MaxText = 400;
    private const int MaxLines = 4;   // written as lines; a long one may wrap to more on a narrow window

    /// <summary>The colours the page has light and dark tones for.</summary>
    internal static readonly string[] Colors = ["accent", "blue", "green", "amber", "orange", "red", "purple", "pink", "grey"];

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // No redirects: the notices come from our address or not at all.
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClaudeTerminals", UpdateService.CurrentVersion.ToString()));
        return http;
    }

    /// <summary>
    /// The notices for this copy of the app today, in the file's order. None when the file or its signature isn't
    /// there, or the signature isn't ours: whoever can change the files on the site still can't write the banner.
    /// </summary>
    public static async Task<IReadOnlyList<Notice>> CheckAsync(CancellationToken ct = default)
    {
        if (FeedUrl == null) return [];   // a build from source, without our storage account
        var json = GetAsync(FeedUrl, ct);
        var signature = GetAsync(FeedUrl + ".p7s", ct);
        if (await json is not { } content || await signature is not { } sig || !Verify(content, sig)) return [];
        return Parse(System.Text.Encoding.UTF8.GetString(content), UpdateService.CurrentVersion, DateTime.UtcNow);
    }

    private static async Task<byte[]?> GetAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Who signs the notices: the company named in our code signing certificate, as on the installer.</summary>
    internal const string Publisher = "Llamachant Technology Ltd";

    private const string CodeSigning = "1.3.6.1.5.5.7.3.3";
    private const string Organization = "2.5.4.10";

    /// <summary>
    /// Whether <paramref name="signature"/> (a detached CMS signature, notices.json.p7s) is <see cref="Publisher"/>'s
    /// over exactly <paramref name="json"/>: the signature checks out, and the certificate is for code signing, issued
    /// to that organisation and chains to a root Windows trusts (or one of <paramref name="trustedRoots"/>, for tests).
    /// It's judged as of when it was signed, so the notices don't vanish when the certificate expires.
    /// </summary>
    internal static bool Verify(byte[] json, byte[] signature, X509Certificate2Collection? trustedRoots = null)
    {
        try
        {
            var cms = new SignedCms(new ContentInfo(json), detached: true);
            cms.Decode(signature);
            if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0] is not { Certificate: { } certificate } signer) return false;
            signer.CheckSignature(verifySignatureOnly: true);   // the content's digest and the signature over it

            if (!certificate.SubjectName.EnumerateRelativeDistinguishedNames().Any(n =>
                    n.GetSingleElementType().Value == Organization && n.GetSingleElementValue() == Publisher))
                return false;

            var signed = signer.SignedAttributes.Cast<CryptographicAttributeObject>().SelectMany(a => a.Values.Cast<AsnEncodedData>())
                .OfType<Pkcs9SigningTime>().Select(t => (DateTime?)t.SigningTime.ToUniversalTime()).FirstOrDefault();
            if (signed > DateTime.UtcNow.AddDays(1)) return false;   // from the future

            using var chain = new X509Chain();
            var policy = chain.ChainPolicy;
            policy.ApplicationPolicy.Add(new Oid(CodeSigning));
            policy.ExtraStore.AddRange(cms.Certificates);
            policy.VerificationTime = (signed ?? DateTime.UtcNow).ToLocalTime();
            if (trustedRoots != null)
            {
                policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                policy.CustomTrustStore.AddRange(trustedRoots);
                policy.RevocationMode = X509RevocationMode.NoCheck;
            }
            else
            {
                // A revoked certificate is refused; one whose status can't be fetched (offline) isn't held against it.
                policy.RevocationMode = X509RevocationMode.Online;
                policy.VerificationFlags = X509VerificationFlags.IgnoreEndRevocationUnknown | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown;
            }
            return chain.Build(certificate);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    /// <summary>
    /// The notices meant for <paramref name="version"/> on <paramref name="now"/>. One that's malformed (no id or
    /// text, an unknown kind, a link that isn't https) is left out rather than shown half-right.
    /// </summary>
    internal static List<Notice> Parse(string json, Version version, DateTime now)
    {
        var found = new List<Notice>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("notices", out var list) || list.ValueKind != JsonValueKind.Array) return found;
        var ids = new HashSet<string>();
        foreach (var n in list.EnumerateArray())
        {
            var id = Str(n, "id");
            // A \n starts a new line; each line's own spaces at the ends go.
            var text = Str(n, "text") is { } t ? string.Join('\n', t.Trim().Split('\n').Select(l => l.Trim())) : null;
            if (text?.Count(c => c == '\n') >= MaxLines) continue;
            var kind = Str(n, "kind") ?? "tip";
            if (id == null || !IdPattern().IsMatch(id) || !ids.Add(id)) continue;
            if (string.IsNullOrEmpty(text) || text.Length > MaxText) continue;
            if (kind is not ("tip" or "offer" or "news")) continue;
            var color = Str(n, "color")?.Trim().ToLowerInvariant() ?? "accent";
            if (!Colors.Contains(color) && !HexColor().IsMatch(color)) continue;

            if (Date(n, "from") is { } from && now < from) continue;
            if (Date(n, "until") is { } until && now >= until.AddDays(1)) continue;   // until is the last day shown
            if (Ver(n, "minVersion") is { } min && version < min) continue;
            if (Ver(n, "maxVersion") is { } max && version > max) continue;

            string? label = null, url = null;
            if (n.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Object)
            {
                url = Str(link, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) continue;
                url = u.AbsoluteUri;
                label = Str(link, "label")?.Trim() is { Length: > 0 } l ? l : "Learn more";
            }
            found.Add(new Notice(id, kind, text, label, url, color));
        }
        return found;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Date(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static Version? Ver(JsonElement e, string name) =>
        Version.TryParse(Str(e, name), out var v) ? UpdateService.Normalize(v) : null;
}
