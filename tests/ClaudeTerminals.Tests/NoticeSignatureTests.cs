using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ClaudeTerminals.Updates;

namespace ClaudeTerminals.Tests;

/// <summary>
/// Only notices signed by our code signing certificate are shown. The certificates here come from a made-up authority
/// trusted for these tests alone; nothing is added to the machine's certificate stores.
/// </summary>
public sealed class NoticeSignatureTests : IDisposable
{
    private static readonly byte[] Json = Encoding.UTF8.GetBytes("""{ "notices": [ { "id": "a", "text": "Hi" } ] }""");

    private readonly X509Certificate2 root = CreateRoot("CN=Test Root");
    private readonly X509Certificate2Collection roots;

    public NoticeSignatureTests() => roots = [root];

    public void Dispose() => root.Dispose();

    [Fact]
    public void Our_signature_over_the_file_is_accepted()
    {
        using var ours = Issue(root, $"CN={NoticeService.Publisher}, O={NoticeService.Publisher}, C=CA");
        Assert.True(NoticeService.Verify(Json, Sign(Json, ours), roots));
    }

    [Fact]
    public void A_file_changed_after_signing_is_refused()
    {
        using var ours = Issue(root, $"CN={NoticeService.Publisher}, O={NoticeService.Publisher}, C=CA");
        var signature = Sign(Json, ours);
        var changed = Encoding.UTF8.GetBytes("""{ "notices": [ { "id": "a", "text": "Buy this" } ] }""");
        Assert.False(NoticeService.Verify(changed, signature, roots));
    }

    [Theory]
    [InlineData("CN=Someone Else, O=Someone Else Ltd, C=CA")]
    [InlineData("CN=Llamachant Technology Ltd, O=Llamachant Technology Ltd Imposters, C=CA")]
    [InlineData("CN=Llamachant Technology Ltd, C=CA")]   // the name only as the common name, which isn't checked
    public void A_certificate_issued_to_anyone_else_is_refused(string subject)
    {
        using var theirs = Issue(root, subject);
        Assert.False(NoticeService.Verify(Json, Sign(Json, theirs), roots));
    }

    [Fact]
    public void A_certificate_not_for_code_signing_is_refused()
    {
        using var server = Issue(root, $"CN={NoticeService.Publisher}, O={NoticeService.Publisher}", "1.3.6.1.5.5.7.3.1");
        Assert.False(NoticeService.Verify(Json, Sign(Json, server), roots));
    }

    [Fact]
    public void A_certificate_from_an_authority_nobody_trusts_is_refused()
    {
        using var stranger = CreateRoot("CN=Untrusted Root");
        using var ours = Issue(stranger, $"CN={NoticeService.Publisher}, O={NoticeService.Publisher}");
        Assert.False(NoticeService.Verify(Json, Sign(Json, ours), roots));
        Assert.False(NoticeService.Verify(Json, Sign(Json, ours)));   // nor by Windows' own roots
    }

    [Fact]
    public void Notices_signed_while_the_certificate_was_valid_still_show_after_it_expires()
    {
        using var expired = Issue(root, $"CN={NoticeService.Publisher}, O={NoticeService.Publisher}",
            notBefore: DateTimeOffset.UtcNow.AddYears(-2), notAfter: DateTimeOffset.UtcNow.AddYears(-1));
        Assert.True(NoticeService.Verify(Json, Sign(Json, expired, signedAt: DateTime.UtcNow.AddYears(-1).AddDays(-30)), roots));
        Assert.False(NoticeService.Verify(Json, Sign(Json, expired, signedAt: DateTime.UtcNow), roots));   // signed after it expired
    }

    [Fact]
    public void Garbage_is_not_a_signature() =>
        Assert.False(NoticeService.Verify(Json, Encoding.UTF8.GetBytes("not a signature"), roots));

    private static X509Certificate2 CreateRoot(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-5), DateTimeOffset.UtcNow.AddYears(5));
    }

    private static X509Certificate2 Issue(X509Certificate2 issuer, string subject, string usage = "1.3.6.1.5.5.7.3.3",
        DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        using var issued = request.Create(issuer, notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(1),
            RandomNumberGenerator.GetBytes(8));
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>As sign-notices does: detached, SHA-256, with the signing time and the certificate.</summary>
    private static byte[] Sign(byte[] content, X509Certificate2 certificate, DateTime? signedAt = null)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(signedAt ?? DateTime.UtcNow));
        cms.ComputeSignature(signer);
        return cms.Encode();
    }
}
