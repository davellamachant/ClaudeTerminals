using System.Text.Json;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>A session set to start as LlamaTree opens: always, or when it was running as LlamaTree last closed.</summary>
public sealed class StartWithAppTests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]      // the default: starts when first opened, whatever ran before
    [InlineData("always", false, true)]
    [InlineData("always", true, true)]
    [InlineData("running", false, false)]
    [InlineData("running", true, true)]
    public void Starts_with_the_app_by_setting_and_whether_it_was_running(string? setting, bool wasRunning, bool starts) =>
        Assert.Equal(starts, new SessionInfo { StartWithApp = setting, WasRunning = wasRunning }.StartsWithApp);

    [Theory]
    [InlineData("always", "always")]
    [InlineData("running", "running")]
    [InlineData("", null)]
    [InlineData("Always", null)]
    [InlineData("whenever", null)]
    [InlineData(null, null)]
    public void Only_known_settings_are_kept(string? fromPage, string? kept) =>
        Assert.Equal(kept, SessionInfo.StartWithAppValue(fromPage));

    [Fact]
    public void The_setting_and_whether_it_ran_are_saved_but_not_the_answer()
    {
        var json = JsonSerializer.Serialize(new SessionInfo { StartWithApp = "running", WasRunning = true });
        Assert.DoesNotContain(nameof(SessionInfo.StartsWithApp), json);
        var back = JsonSerializer.Deserialize<SessionInfo>(json)!;
        Assert.Equal("running", back.StartWithApp);
        Assert.True(back.WasRunning);
    }

    [Fact]
    public void A_session_saved_before_the_setting_starts_when_first_opened()
    {
        var old = JsonSerializer.Deserialize<SessionInfo>("""{"Id":"a","Name":"A","Directory":"C:\\x","Command":"claude"}""")!;
        Assert.Null(old.StartWithApp);
        Assert.False(old.StartsWithApp);
    }
}
