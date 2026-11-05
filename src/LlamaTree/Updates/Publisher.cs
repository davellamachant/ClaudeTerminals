using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace LlamaTree.Updates;

/// <summary>
/// Who LlamaTree comes from, and the checks that something is really theirs: the notices along the top, the signed list
/// of each release's checksums (SHA256SUMS.p7s) and, on Windows, the installer's own signature.
/// <para>
/// What's pinned survives renewing the certificate: the company as the certificate names it (its name, and its number
/// and country of registration, which an EV certificate carries) and the authority's root, Sectigo Public Code Signing
/// Root R46 (valid to 2046), by its key. Not the certificate itself, which is replaced every few years. Moving to
/// another authority, or to a certificate without the registration details, needs a release that accepts the new
/// one first, signed with the old: copies only update to what the copy they have accepts.
/// </para>
/// </summary>
internal static class Publisher
{
    /// <summary>The organisation (O=) the certificate is issued to.</summary>
    internal const string Name = "Llamachant Technology Ltd";

    /// <summary>Its registration (SERIALNUMBER=, Ontario's corporation number) and where (jurisdictionCountryName=).</summary>
    internal const string RegistrationNumber = "2469513";
    internal const string Jurisdiction = "CA";

    internal const string CodeSigning = "1.3.6.1.5.5.7.3.3";
    private const string Organization = "2.5.4.10";
    private const string SerialNumber = "2.5.4.5";
    private const string JurisdictionCountry = "1.3.6.1.4.1.311.60.2.1.3";

    /// <summary>Sectigo Public Code Signing Root R46, which our code signing certificate chains to.</summary>
    private const string RootR46 =
        "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";

    /// <summary>The roots our certificate may chain to (new instances each time: callers may dispose them).</summary>
    internal static X509Certificate2Collection Roots() => [X509CertificateLoader.LoadCertificate(Convert.FromBase64String(RootR46))];

    /// <summary>
    /// Whether <paramref name="certificate"/> is our code signing certificate: for code signing, and issued to us by
    /// name and registration. Whether it chains to <see cref="Roots"/> is checked separately.
    /// </summary>
    internal static bool IsOurs(X509Certificate2 certificate)
    {
        string? Field(string oid) => certificate.SubjectName.EnumerateRelativeDistinguishedNames()
            .Where(n => !n.HasMultipleElements && n.GetSingleElementType().Value == oid).Select(n => n.GetSingleElementValue()).SingleOrDefault();
        try
        {
            return Field(Organization) == Name && Field(SerialNumber) == RegistrationNumber && Field(JurisdictionCountry) == Jurisdiction
                && certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == CodeSigning));
        }
        catch (InvalidOperationException) { return false; }   // the same field twice
    }

    /// <summary>
    /// Whether <paramref name="signature"/> (a detached CMS signature, as tools/sign-notices.cs and sign-checksums.cs
    /// write) is ours over exactly <paramref name="content"/>: the signature checks out, the certificate is
    /// <see cref="IsOurs">ours</see> and chains to one of <paramref name="roots"/> (by default <see cref="Roots"/>; only
    /// tests pass others), on any system, whatever roots it trusts itself. It's judged as of when it was signed, so
    /// what was signed doesn't stop counting when the certificate expires.
    /// </summary>
    internal static bool VerifyDetached(byte[] content, byte[] signature, X509Certificate2Collection? roots = null)
    {
        try
        {
            var cms = new SignedCms(new ContentInfo(content), detached: true);
            cms.Decode(signature);
            if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0] is not { Certificate: { } certificate } signer) return false;
            signer.CheckSignature(verifySignatureOnly: true);   // the content's digest and the signature over it
            if (!IsOurs(certificate)) return false;

            var signed = signer.SignedAttributes.Cast<CryptographicAttributeObject>().SelectMany(a => a.Values.Cast<AsnEncodedData>())
                .OfType<Pkcs9SigningTime>().Select(t => (DateTime?)t.SigningTime.ToUniversalTime()).FirstOrDefault();
            if (signed > DateTime.UtcNow.AddDays(1)) return false;   // from the future

            var pinned = roots ?? Roots();
            using var chain = new X509Chain();
            var policy = chain.ChainPolicy;
            policy.ApplicationPolicy.Add(new Oid(CodeSigning));
            policy.ExtraStore.AddRange(cms.Certificates);
            policy.VerificationTime = (signed ?? DateTime.UtcNow).ToLocalTime();
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.AddRange(pinned);
            if (roots != null) policy.RevocationMode = X509RevocationMode.NoCheck;   // made-up authorities publish no lists
            else
            {
                // A revoked certificate is refused; one whose status can't be fetched (offline) isn't held against it.
                policy.RevocationMode = X509RevocationMode.Online;
                policy.VerificationFlags = X509VerificationFlags.IgnoreEndRevocationUnknown | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown;
            }
            try { return chain.Build(certificate); }
            finally { if (roots == null) foreach (var r in pinned) r.Dispose(); }
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> carries a valid Authenticode signature (Windows' own check:
    /// unchanged since signing, a trusted chain, timestamped if the certificate has since expired, not revoked) by
    /// <see cref="IsOurs">our certificate</see>, through one of <paramref name="roots"/> (by default <see cref="Roots"/>).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool IsSignedByUs(string path, X509Certificate2Collection? roots = null)
    {
        var chain = Authenticode.VerifiedSignerChain(path);
        if (chain == null) return false;
        var pinned = roots ?? Roots();
        try { return IsOursThrough(chain, pinned); }
        finally
        {
            foreach (var c in chain) c.Dispose();
            if (roots == null) foreach (var r in pinned) r.Dispose();
        }
    }

    /// <summary>
    /// Whether a signer's chain (the signer first, as Windows built it) is ours: the signer <see cref="IsOurs">is
    /// ours</see> and the chain goes through a certificate with the key of one of <paramref name="roots"/>. By key, so the
    /// root counts whether Windows reached it as itself or cross-signed by an older root.
    /// </summary>
    internal static bool IsOursThrough(IReadOnlyList<X509Certificate2> chain, X509Certificate2Collection roots)
    {
        if (chain.Count == 0 || !IsOurs(chain[0])) return false;
        var keys = roots.Select(r => Convert.ToHexString(r.PublicKey.ExportSubjectPublicKeyInfo())).ToHashSet();
        return chain.Skip(1).Any(c => keys.Contains(Convert.ToHexString(c.PublicKey.ExportSubjectPublicKeyInfo())));
    }
}
