// Writes a release's SHA256SUMS (sha256sum's format: "<hash>  <name>" for each file) and SHA256SUMS.p7s, a detached
// CMS signature of it by the company's code signing certificate (from the Windows certificate store, by subject; on the
// build agent the SafeNet token provides it), as tools/sign-notices.cs signs the notices.
// The app installs an update only when the download matches its line here and this signature is ours (see
// src/LlamaTree/Updates/Publisher.cs): a release changed on GitHub after it was built, by anyone who can publish there,
// isn't installed. Anyone can check by hand too: sha256sum -c SHA256SUMS --ignore-missing.
//
//   dotnet run tools/sign-checksums.cs -- <folder for SHA256SUMS> <file>... [--subject <subject>]
//
// Every file attached to the release goes in: copies look up the one they install by its name.
#:package System.Security.Cryptography.Pkcs@10.0.14

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

// As Publisher.cs in the app: the certificate must name the company, and its registration, as these.
const string Organization = "2.5.4.10";
const string SerialNumber = "2.5.4.5";
const string JurisdictionCountry = "1.3.6.1.4.1.311.60.2.1.3";
const string CodeSigning = "1.3.6.1.5.5.7.3.3";
const string RegistrationNumber = "2469513";
const string Jurisdiction = "CA";

var subject = "Llamachant Technology Ltd";   // as publish.ps1, and Publisher.Name
var rest = args.ToList();
if (rest.IndexOf("--subject") is var at and >= 0 && at + 1 < rest.Count) { subject = rest[at + 1]; rest.RemoveRange(at, 2); }
if (rest.Count < 2) { Console.Error.WriteLine("Usage: dotnet run tools/sign-checksums.cs -- <folder for SHA256SUMS> <file>... [--subject <subject>]"); return 2; }
var outDir = Path.GetFullPath(rest[0]);
var files = rest.Skip(1).Select(Path.GetFullPath).ToList();

var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var sums = new StringBuilder();
foreach (var file in files)
{
    var name = Path.GetFileName(file);
    if (!names.Add(name)) throw new InvalidOperationException($"Two files named {name}: copies couldn't tell which is which.");
    using var stream = File.OpenRead(file);
    sums.Append(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()).Append("  ").Append(name).Append('\n');
}
var content = Encoding.UTF8.GetBytes(sums.ToString());

string? Field(X509Certificate2 c, string oid) => c.SubjectName.EnumerateRelativeDistinguishedNames()
    .Where(n => !n.HasMultipleElements && n.GetSingleElementType().Value == oid).Select(n => n.GetSingleElementValue()).FirstOrDefault();

// The newest valid code signing certificate with its key, issued to the organisation and its registration, as the app requires.
var certificate = new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine }
    .SelectMany(location =>
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Find(X509FindType.FindBySubjectName, subject, validOnly: true).Cast<X509Certificate2>().ToList();
    })
    .Where(c => c.HasPrivateKey
        && c.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == CodeSigning))
        && Field(c, Organization) == subject && Field(c, SerialNumber) == RegistrationNumber && Field(c, JurisdictionCountry) == Jurisdiction)
    .OrderByDescending(c => c.NotAfter)
    .FirstOrDefault()
    ?? throw new InvalidOperationException($"No valid code signing certificate with its private key for O={subject}, SERIALNUMBER={RegistrationNumber} in the certificate store.");
Console.WriteLine($"Signing SHA256SUMS ({files.Count} files) as {certificate.Subject} (expires {certificate.NotAfter:yyyy-MM-dd})");

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
Directory.CreateDirectory(outDir);
File.WriteAllBytes(Path.Combine(outDir, "SHA256SUMS"), content);
File.WriteAllBytes(Path.Combine(outDir, "SHA256SUMS.p7s"), signature);
Console.Write(sums);
Console.WriteLine($"Wrote {Path.Combine(outDir, "SHA256SUMS")} and SHA256SUMS.p7s");
return 0;
