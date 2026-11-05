using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LlamaTree.Updates;

namespace LlamaTree.Tests;

/// <summary>
/// An update is installed only when the download is the one we signed for the release: it matches its line in the
/// release's SHA256SUMS, that list carries our signature, and on Windows the installer carries our Authenticode
/// signature too. The certificates are made up for these tests and trusted by them alone.
/// </summary>
public sealed class UpdateSignatureTests : IDisposable
{
    private const string Installer = "LlamaTree-Setup-1.4.0.exe";

    private readonly X509Certificate2 root = TestCertificates.CreateRoot("CN=Test Root");
    private readonly X509Certificate2Collection roots;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "llamatree-update-test-" + Guid.NewGuid().ToString("N")[..8]);

    public UpdateSignatureTests()
    {
        roots = [root];
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        root.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private string Download(string content = "the real installer")
    {
        var path = Path.Combine(dir, Installer);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static UpdateInfo Info(string sha, Version? version = null) =>
        new(version ?? new Version(1, 4, 0), "LlamaTree 1.4.0", "https://github.com/x", "", Installer, "https://github.com/x/" + Installer, 0, sha);

    private byte[] Sums(params (string Name, string Content)[] files) =>
        Encoding.UTF8.GetBytes(string.Concat(files.Select(f => $"{Sha(f.Content)}  {f.Name}\n")));

    // The checksums

    [Fact]
    public void Checksums_we_signed_are_read()
    {
        using var ours = TestCertificates.Issue(root, TestCertificates.Ours());
        var sums = Sums((Installer, "the real installer"), ("install.sh", "#!/bin/sh"));

        var read = UpdateService.VerifiedChecksums(sums, TestCertificates.Sign(sums, ours), roots);

        Assert.NotNull(read);
        Assert.Equal(Sha("the real installer"), read[Installer]);
        Assert.Equal(2, read.Count);
    }

    [Fact]
    public void Checksums_changed_after_signing_are_refused()
    {
        using var ours = TestCertificates.Issue(root, TestCertificates.Ours());
        var signature = TestCertificates.Sign(Sums((Installer, "the real installer")), ours);
        Assert.Null(UpdateService.VerifiedChecksums(Sums((Installer, "something else")), signature, roots));
    }

    [Fact]
    public void Checksums_signed_by_anyone_else_are_refused()
    {
        using var theirs = TestCertificates.Issue(root, TestCertificates.Subject("Someone Else Ltd", "999", "CA"));
        using var stranger = TestCertificates.CreateRoot("CN=Another Root");
        using var lookalike = TestCertificates.Issue(stranger, TestCertificates.Ours());   // our name, from an authority not pinned
        var sums = Sums((Installer, "evil"));

        Assert.Null(UpdateService.VerifiedChecksums(sums, TestCertificates.Sign(sums, theirs), roots));
        Assert.Null(UpdateService.VerifiedChecksums(sums, TestCertificates.Sign(sums, lookalike), roots));
        Assert.Null(UpdateService.VerifiedChecksums(sums, TestCertificates.Sign(sums, lookalike)));   // the real pinned root
    }

    [Fact]
    public void Checksums_are_read_in_sha256sums_format()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var sums = UpdateService.ParseChecksums($"{a}  one.exe\r\n{b} *two.deb\nnot a line\n{a}  twice\n{b}  twice\n{a}  ../escape\n");
        Assert.Equal(a, sums["one.exe"]);
        Assert.Equal(b.ToLowerInvariant(), sums["two.deb"]);
        Assert.False(sums.ContainsKey("twice"));   // listed twice: neither counts
        Assert.Equal(2, sums.Count);
    }

    // The download

    [Fact]
    public void The_installer_we_signed_is_installed()
    {
        var sha = Sha("the real installer");
        var signed = new Dictionary<string, string> { [Installer] = sha };
        Assert.Null(UpdateService.Refusal(Info(sha), Download(), signed, _ => true));
    }

    [Fact]
    public void A_tampered_installer_is_refused()
    {
        var sha = Sha("the real installer");
        var signed = new Dictionary<string, string> { [Installer] = sha };
        Assert.NotNull(UpdateService.Refusal(Info(sha), Download("the real installer, with a little extra"), signed, _ => true));
    }

    [Fact]
    public void An_installer_replaced_on_GitHub_with_its_checksum_is_refused()
    {
        // Whoever replaced it changed GitHub's checksum to match, but can't change the signed one.
        var evil = Sha("evil");
        var signed = new Dictionary<string, string> { [Installer] = Sha("the real installer") };
        var refusal = UpdateService.Refusal(Info(evil), Download("evil"), signed, _ => true);
        Assert.Contains(Publisher.Name, refusal);
    }

    [Fact]
    public void An_installer_missing_from_the_signed_checksums_is_refused()
    {
        // As when an older release's signed list is put on a new release: the names carry the version.
        var sha = Sha("the real installer");
        var signed = new Dictionary<string, string> { ["LlamaTree-Setup-1.3.9.exe"] = sha };
        Assert.NotNull(UpdateService.Refusal(Info(sha), Download(), signed, _ => true));
    }

    [Fact]
    public void A_release_without_signed_checksums_is_refused()
    {
        var sha = Sha("the real installer");
        Assert.NotNull(UpdateService.Refusal(Info(sha), Download(), null, _ => true));
        // Only one from before signed checksums existed goes without (never offered: it's older than this copy).
        Assert.Null(UpdateService.Refusal(Info(sha, new Version(1, 3, 0)), Download(), null, _ => true));
    }

    [Fact]
    public void An_installer_without_our_authenticode_signature_is_refused()
    {
        var sha = Sha("the real installer");
        var signed = new Dictionary<string, string> { [Installer] = sha };
        var refusal = UpdateService.Refusal(Info(sha), Download(), signed, _ => false);
        Assert.Contains("isn't signed by", refusal);
    }

    [WindowsFact]
    public void An_unsigned_installer_is_refused_on_windows()
    {
        // The real check, not a stand-in: a file that isn't signed at all.
        var sha = Sha("the real installer");
        var signed = new Dictionary<string, string> { [Installer] = sha };
        Assert.NotNull(UpdateService.Refusal(Info(sha), Download(), signed));
    }

    [Fact]
    public void A_release_lists_its_signed_checksums()
    {
        var json = JsonSerializer.Serialize(new
        {
            tag_name = "v1.4.0", name = "LlamaTree 1.4.0", body = "", draft = false, prerelease = false,
            assets = new object[]
            {
                new { name = Installer, size = 1, digest = "sha256:" + new string('a', 64), browser_download_url = "https://github.com/r/" + Installer },
                new { name = "SHA256SUMS", size = 1, digest = (string?)null, browser_download_url = "https://github.com/r/SHA256SUMS" },
                new { name = "SHA256SUMS.p7s", size = 1, digest = (string?)null, browser_download_url = "https://github.com/r/SHA256SUMS.p7s" },
            },
        });
        var u = UpdateService.Parse(json, InstallKind.Windows);
        Assert.Equal("https://github.com/r/SHA256SUMS", u?.ChecksumsUrl);
        Assert.Equal("https://github.com/r/SHA256SUMS.p7s", u?.ChecksumsSignatureUrl);
    }

    // The Authenticode signature (Windows' own check)

    /// <summary>A file of the .NET runtime this test runs on, Authenticode-signed by Microsoft.</summary>
    private static string MicrosoftSigned => Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");

    [WindowsFact]
    public void Windows_accepts_a_valid_signature_and_says_who_signed()
    {
        var chain = Authenticode.VerifiedSignerChain(MicrosoftSigned);
        Assert.NotNull(chain);
        try
        {
            Assert.Contains("O=Microsoft Corporation", chain[0].Subject);
            Assert.True(chain.Length >= 2);
        }
        finally { foreach (var c in chain) c.Dispose(); }
    }

    [WindowsFact]
    public void A_valid_signature_by_someone_else_is_refused() =>
        Assert.False(Publisher.IsSignedByUs(MicrosoftSigned));

    [WindowsFact]
    public void A_signed_file_changed_afterwards_is_refused()
    {
        var copy = Path.Combine(dir, "tampered.dll");
        var bytes = File.ReadAllBytes(MicrosoftSigned);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(copy, bytes);
        Assert.Null(Authenticode.VerifiedSignerChain(copy));
    }

    [WindowsFact]
    public void An_unsigned_file_has_no_signer() =>
        Assert.Null(Authenticode.VerifiedSignerChain(Download()));

    [Fact]
    public void A_signer_chain_is_ours_only_with_our_certificate_through_the_pinned_root()
    {
        using var intermediate = TestCertificates.CreateRoot("CN=Test Intermediate");
        using var ours = TestCertificates.Issue(root, TestCertificates.Ours());
        using var theirs = TestCertificates.Issue(root, TestCertificates.Subject("Someone Else Ltd", "1", "CA"));
        using var stranger = TestCertificates.CreateRoot("CN=Another Root");
        using var lookalike = TestCertificates.Issue(stranger, TestCertificates.Ours());

        Assert.True(Publisher.IsOursThrough([ours, root], roots));
        Assert.True(Publisher.IsOursThrough([ours, intermediate, root], roots));
        Assert.False(Publisher.IsOursThrough([theirs, root], roots));         // not us
        Assert.False(Publisher.IsOursThrough([lookalike, stranger], roots));  // our name, through another authority
        Assert.False(Publisher.IsOursThrough([ours], roots));                 // the root only as the signer itself doesn't count
        Assert.False(Publisher.IsOursThrough([lookalike, stranger], Publisher.Roots()));
    }
}
