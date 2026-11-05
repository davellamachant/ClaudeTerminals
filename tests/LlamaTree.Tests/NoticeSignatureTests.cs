using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LlamaTree.Updates;

namespace LlamaTree.Tests;

/// <summary>
/// Only notices signed by our code signing certificate are shown. The certificates here come from a made-up authority
/// trusted for these tests alone; nothing is added to the machine's certificate stores.
/// </summary>
public sealed class NoticeSignatureTests : IDisposable
{
    private static readonly byte[] Json = Encoding.UTF8.GetBytes("""{ "notices": [ { "id": "a", "text": "Hi" } ] }""");

    private readonly X509Certificate2 root = TestCertificates.CreateRoot("CN=Test Root");
    private readonly X509Certificate2Collection roots;

    public NoticeSignatureTests() => roots = [root];

    public void Dispose() => root.Dispose();

    [Fact]
    public void Our_signature_over_the_file_is_accepted()
    {
        using var ours = TestCertificates.Issue(root, TestCertificates.Ours());
        Assert.True(Publisher.VerifyDetached(Json, TestCertificates.Sign(Json, ours), roots));
    }

    [Fact]
    public void A_file_changed_after_signing_is_refused()
    {
        using var ours = TestCertificates.Issue(root, TestCertificates.Ours());
        var signature = TestCertificates.Sign(Json, ours);
        var changed = Encoding.UTF8.GetBytes("""{ "notices": [ { "id": "a", "text": "Buy this" } ] }""");
        Assert.False(Publisher.VerifyDetached(changed, signature, roots));
    }

    [Theory]
    [InlineData("Someone Else Ltd", Publisher.RegistrationNumber, Publisher.Jurisdiction)]
    [InlineData(Publisher.Name + " Imposters", Publisher.RegistrationNumber, Publisher.Jurisdiction)]
    [InlineData(null, Publisher.RegistrationNumber, Publisher.Jurisdiction)]   // the name only as the common name, which isn't checked
    [InlineData(Publisher.Name, "1234567", Publisher.Jurisdiction)]            // a company of the same name, registered elsewhere
    [InlineData(Publisher.Name, Publisher.RegistrationNumber, "GB")]
    [InlineData(Publisher.Name, null, null)]                                   // no registration details at all
    public void A_certificate_issued_to_anyone_else_is_refused(string? organization, string? registration, string? jurisdiction)
    {
        using var theirs = TestCertificates.Issue(root, TestCertificates.Subject(organization, registration, jurisdiction));
        Assert.False(Publisher.VerifyDetached(Json, TestCertificates.Sign(Json, theirs), roots));
    }

    [Fact]
    public void A_certificate_not_for_code_signing_is_refused()
    {
        using var server = TestCertificates.Issue(root, TestCertificates.Ours(), "1.3.6.1.5.5.7.3.1");
        Assert.False(Publisher.VerifyDetached(Json, TestCertificates.Sign(Json, server), roots));
    }

    [Fact]
    public void A_certificate_from_another_authority_is_refused()
    {
        using var stranger = TestCertificates.CreateRoot("CN=Untrusted Root");
        using var ours = TestCertificates.Issue(stranger, TestCertificates.Ours());
        Assert.False(Publisher.VerifyDetached(Json, TestCertificates.Sign(Json, ours), roots));
        Assert.False(Publisher.VerifyDetached(Json, TestCertificates.Sign(Json, ours)));   // nor by the pinned root
    }

    [Fact]
    public void Notices_signed_while_the_certificate_was_valid_still_show_after_it_expires()
    {
        using var expired = TestCertificates.Issue(root, TestCertificates.Ours(),
            notBefore: DateTimeOffset.UtcNow.AddYears(-2), notAfter: DateTimeOffset.UtcNow.AddYears(-1));
        Assert.True(Publisher.VerifyDetached(Json, TestCertificates.Sign(Json, expired, signedAt: DateTime.UtcNow.AddYears(-1).AddDays(-30)), roots));
        Assert.False(Publisher.VerifyDetached(Json, TestCertificates.Sign(Json, expired, signedAt: DateTime.UtcNow), roots));   // signed after it expired
    }

    [Fact]
    public void Garbage_is_not_a_signature() =>
        Assert.False(Publisher.VerifyDetached(Json, Encoding.UTF8.GetBytes("not a signature"), roots));

    [Fact]
    public void The_pinned_root_is_sectigos_code_signing_root()
    {
        var pinned = Publisher.Roots();
        using var r46 = Assert.Single(pinned);
        Assert.Equal("CN=Sectigo Public Code Signing Root R46, O=Sectigo Limited, C=GB", r46.Subject);
        Assert.Equal("CCBBF9E1485AF63CE47ABF8E9E648C2504FC319D", r46.Thumbprint);
    }
}

/// <summary>Made-up certificates for the signature tests: an authority, and certificates it issues.</summary>
internal static class TestCertificates
{
    /// <summary>A subject as our certificate's: the company by name, registration and jurisdiction (any null left out).</summary>
    public static X500DistinguishedName Subject(string? organization, string? registration, string? jurisdiction)
    {
        var name = new X500DistinguishedNameBuilder();
        name.AddCountryOrRegion("CA");
        if (organization != null) name.AddOrganizationName(organization);
        name.AddCommonName(Publisher.Name);
        if (registration != null) name.Add("2.5.4.5", registration, UniversalTagNumber.PrintableString);
        if (jurisdiction != null) name.Add("1.3.6.1.4.1.311.60.2.1.3", jurisdiction, UniversalTagNumber.PrintableString);
        return name.Build();
    }

    public static X500DistinguishedName Ours() => Subject(Publisher.Name, Publisher.RegistrationNumber, Publisher.Jurisdiction);

    public static X509Certificate2 CreateRoot(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-5), DateTimeOffset.UtcNow.AddYears(5));
    }

    public static X509Certificate2 Issue(X509Certificate2 issuer, X500DistinguishedName subject, string usage = "1.3.6.1.5.5.7.3.3",
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

    /// <summary>As tools/sign-notices.cs and sign-checksums.cs do: detached, SHA-256, with the signing time and the certificate.</summary>
    public static byte[] Sign(byte[] content, X509Certificate2 certificate, DateTime? signedAt = null)
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
