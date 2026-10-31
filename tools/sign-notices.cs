// Signs notices.json for the banner: writes notices.json.p7s, a detached CMS signature by the company's code signing
// certificate (from the Windows certificate store, by subject; on the build agent the SafeNet token provides it).
// The app shows the notices only with this signature, from a certificate issued to the organisation below.
//
//   dotnet run tools/sign-notices.cs -- notices.json [subject]
//
// Upload notices.json and notices.json.p7s together. Change notices.json after signing and the app ignores it.
#:package System.Security.Cryptography.Pkcs@10.0.14

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

const string Organization = "2.5.4.10";
const string CodeSigning = "1.3.6.1.5.5.7.3.3";

if (args.Length < 1) { Console.Error.WriteLine("Usage: dotnet run tools/sign-notices.cs -- <notices.json> [subject]"); return 2; }
var path = Path.GetFullPath(args[0]);
var subject = args.Length > 1 ? args[1] : "Llamachant Technology Ltd";   // as publish.ps1, and NoticeService.Publisher

var content = File.ReadAllBytes(path);
using (var doc = JsonDocument.Parse(content))   // never sign a file the app can't read
    if (!doc.RootElement.TryGetProperty("notices", out var list) || list.ValueKind != JsonValueKind.Array)
        throw new InvalidDataException($"{path} has no \"notices\" list.");

// The newest valid code signing certificate with its key, issued to the organisation (O=), as the app requires.
var certificate = new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine }
    .SelectMany(location =>
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Find(X509FindType.FindBySubjectName, subject, validOnly: true).Cast<X509Certificate2>().ToList();
    })
    .Where(c => c.HasPrivateKey
        && c.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == CodeSigning))
        && c.SubjectName.EnumerateRelativeDistinguishedNames().Any(n => n.GetSingleElementType().Value == Organization && n.GetSingleElementValue() == subject))
    .OrderByDescending(c => c.NotAfter)
    .FirstOrDefault()
    ?? throw new InvalidOperationException($"No valid code signing certificate with its private key for O={subject} in the certificate store.");
Console.WriteLine($"Signing {Path.GetFileName(path)} as {certificate.Subject} (expires {certificate.NotAfter:yyyy-MM-dd})");

var cms = new SignedCms(new ContentInfo(content), detached: true);
var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
{
    DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),   // SHA-256
    IncludeOption = X509IncludeOption.ExcludeRoot,          // the intermediates, so the app can build the chain
};
signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
cms.ComputeSignature(signer, silent: false);
var signature = cms.Encode();

// Read it back as the app will before writing it.
var check = new SignedCms(new ContentInfo(content), detached: true);
check.Decode(signature);
check.CheckSignature(verifySignatureOnly: false);
File.WriteAllBytes(path + ".p7s", signature);
Console.WriteLine($"Wrote {path}.p7s");
return 0;
