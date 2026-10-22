using System.Text.Json;
using ClaudeTerminals.Sessions;
using ClaudeTerminals.Updates;

namespace ClaudeTerminals.Tests;

/// <summary>Reading GitHub's latest release, and carrying sessions' conversations across the restart an update causes.</summary>
public sealed class UpdateServiceTests
{
    private const string Hash = "6cbe260bf8bb8fb717de70f8c4f4030045dd05fd7340c84ab78561d108017f48";
    private const string Other = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string Release(string tag = "v1.0.8", string? digest = "sha256:" + Hash, string notes = "### New\n- Things",
        string asset = "ClaudeTerminals-Setup-1.0.8.exe", bool prerelease = false) => JsonSerializer.Serialize(new
    {
        tag_name = tag, name = "Claude Terminals 1.0.8", html_url = "https://github.com/davellamachant/ClaudeTerminals/releases/tag/" + tag,
        body = notes, draft = false, prerelease,
        assets = new object[]
        {
            new { name = "notes.txt", size = 10, digest = (string?)null, browser_download_url = "https://github.com/x/notes.txt" },
            new { name = asset, size = 41932242, digest, browser_download_url = "https://github.com/davellamachant/ClaudeTerminals/releases/download/" + tag + "/" + asset },
        },
    });

    [Fact]
    public void The_installer_and_GitHubs_checksum_are_read_from_the_release()
    {
        var u = UpdateService.Parse(Release());

        Assert.NotNull(u);
        Assert.Equal(new Version(1, 0, 8), u.Version);
        Assert.Equal("ClaudeTerminals-Setup-1.0.8.exe", u.AssetName);
        Assert.EndsWith("/releases/download/v1.0.8/ClaudeTerminals-Setup-1.0.8.exe", u.AssetUrl);
        Assert.Equal(41932242, u.Size);
        Assert.Equal(Hash, u.Sha256);
    }

    [Fact]
    public void Without_GitHubs_digest_the_checksum_in_the_notes_is_used()
    {
        var u = UpdateService.Parse(Release(digest: null, notes: $"### Install\n**SHA-256** `x.exe`\n```\n{Hash.ToUpperInvariant()}\n```"));
        Assert.Equal(Hash, u?.Sha256);
    }

    [Fact]
    public void Two_different_checksums_mean_neither_is_trusted()
    {
        var u = UpdateService.Parse(Release(notes: $"**SHA-256**\n```\n{Other}\n```"));
        Assert.NotNull(u);
        Assert.Null(u.Sha256);   // shown, but can't be installed automatically
    }

    [Fact]
    public void No_checksum_at_all_means_it_cant_be_installed_automatically() =>
        Assert.Null(UpdateService.Parse(Release(digest: null, notes: "no hash here"))?.Sha256);

    [Theory]
    [InlineData("v1.0.8", "ClaudeTerminals-Setup-1.0.7.exe")]   // the installer doesn't match the version
    [InlineData("latest", "ClaudeTerminals-Setup-1.0.8.exe")]   // not a version
    public void A_release_without_its_own_installer_or_version_is_not_offered(string tag, string asset) =>
        Assert.Null(UpdateService.Parse(Release(tag: tag, asset: asset)));

    [Fact]
    public void A_prerelease_is_not_offered() => Assert.Null(UpdateService.Parse(Release(prerelease: true)));

    [Fact]
    public void The_service_initialises_and_knows_this_copys_version()
    {
        // Touching a static field runs all of the class's static initializers (including the HTTP client's).
        var v = UpdateService.CurrentVersion;
        Assert.NotNull(v);
        Assert.Equal(-1, v.Revision);   // three parts, as release tags have
    }

    [Fact]
    public void Versions_compare_without_a_fourth_part()
    {
        Assert.Equal(new Version(1, 0, 7), UpdateService.Normalize(new Version(1, 0, 7, 0)));
        Assert.True(UpdateService.Normalize(new Version(1, 0, 10)) > UpdateService.Normalize(new Version(1, 0, 9, 0)));
        Assert.Equal(new Version(2, 0, 0), UpdateService.Normalize(new Version(2, 0)));
    }

    [Theory]
    [InlineData("claude", "claude --resume abc-123")]
    [InlineData("claude --model opus", "claude --model opus --resume abc-123")]
    [InlineData("Claude.exe", "Claude.exe --resume abc-123")]
    [InlineData("claude --resume other", "claude --resume other")]     // already picks its conversation
    [InlineData("claude -c", "claude -c")]
    [InlineData("claude --continue", "claude --continue")]
    [InlineData("claude -p hello", "claude -p hello")]
    [InlineData("pwsh", "pwsh")]                                       // not Claude
    [InlineData("node fake.mjs", "node fake.mjs")]
    public void After_an_update_a_plain_claude_session_resumes_its_conversation(string command, string expected) =>
        Assert.Equal(expected, TerminalSession.WithResume(command, "abc-123"));

    [Theory]
    [InlineData(null)]
    [InlineData("abc 123")]
    [InlineData("abc&calc")]
    [InlineData("\"quoted\"")]
    public void Only_a_plain_conversation_id_is_ever_added(string? id) =>
        Assert.Equal("claude", TerminalSession.WithResume("claude", id));
}
