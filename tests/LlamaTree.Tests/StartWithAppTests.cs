using System.Text.Json;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>
/// A session set to start as LlamaTree opens: always, or when it was running as LlamaTree last closed. A session with no
/// setting of its own follows its project's.
/// </summary>
public sealed class StartWithAppTests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]      // the default: starts when first opened, whatever ran before
    [InlineData("click", true, false)]
    [InlineData("always", false, true)]
    [InlineData("always", true, true)]
    [InlineData("running", false, false)]
    [InlineData("running", true, true)]
    public void Starts_with_the_app_by_setting_and_whether_it_was_running(string? setting, bool wasRunning, bool starts) =>
        Assert.Equal(starts, new SessionInfo { StartWithApp = setting, WasRunning = wasRunning }.StartsWithApp());

    [Theory]
    [InlineData(null, "always", false, true)]     // no setting of its own: its project's
    [InlineData(null, "running", true, true)]
    [InlineData(null, "running", false, false)]
    [InlineData(null, null, true, false)]         // a project with none: when first opened
    [InlineData("click", "always", true, false)]  // its own wins over its project's
    [InlineData("always", null, false, true)]
    [InlineData("running", "always", false, false)]
    public void A_session_without_its_own_setting_follows_its_project(string? own, string? project, bool wasRunning, bool starts) =>
        Assert.Equal(starts, new SessionInfo { StartWithApp = own, WasRunning = wasRunning }.StartsWithApp(project));

    [Theory]
    [InlineData("always", "always")]
    [InlineData("running", "running")]
    [InlineData("click", "click")]
    [InlineData("", null)]
    [InlineData("Always", null)]
    [InlineData("whenever", null)]
    [InlineData(null, null)]
    public void Only_known_settings_are_kept(string? fromPage, string? kept) =>
        Assert.Equal(kept, SessionInfo.StartWithAppValue(fromPage));

    [Theory]
    [InlineData("always", "always")]
    [InlineData("running", "running")]
    [InlineData("click", null)]   // a project's "when first opened" is no setting
    [InlineData("", null)]
    [InlineData("whenever", null)]
    public void Only_known_project_settings_are_kept(string? fromPage, string? kept) =>
        Assert.Equal(kept, ProjectInfo.StartWithAppValue(fromPage));

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
        Assert.False(old.StartsWithApp());
    }

    [Fact]
    public void A_projects_setting_is_saved_and_an_unknown_one_dropped_on_loading()
    {
        // A folder of its own: saving makes the file's folder private, which a shared one like /tmp refuses.
        var folder = Path.Combine(Path.GetTempPath(), $"start-proj-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "projects.json");
        try
        {
            ProjectStore.Save(path, [
                new ProjectInfo { Id = "a", Name = "A", StartWithApp = "always" },
                new ProjectInfo { Id = "b", Name = "B", StartWithApp = "whenever" },
                new ProjectInfo { Id = "c", Name = "C" },
            ]);
            var loaded = ProjectStore.Load(path).ToDictionary(p => p.Id, p => p.StartWithApp);
            Assert.Equal("always", loaded["a"]);
            Assert.Null(loaded["b"]);
            Assert.Null(loaded["c"]);
        }
        finally { Directory.Delete(folder, true); }
    }
}
