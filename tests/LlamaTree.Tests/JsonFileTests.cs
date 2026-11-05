using LlamaTree.Panels;
using LlamaTree.Platform;
using LlamaTree.Plugins;
using LlamaTree.Relay;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>
/// The app's JSON files (sessions.json and the rest): a damaged one is set aside instead of saved over, the copy before
/// it is used where that's safe, and null entries are dropped instead of stopping the app.
/// </summary>
public sealed class JsonFileTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ct-json-" + Guid.NewGuid().ToString("N")[..8]);
    private string Sessions => Path.Combine(folder, "sessions.json");

    public JsonFileTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static SessionInfo Session(string name) => new() { Name = name, Directory = @"D:\repo", Command = "claude" };

    private string[] SetAside(string file) => Directory.GetFiles(folder, Path.GetFileName(file) + ".corrupt-*");

    [Fact]
    public void A_corrupt_sessions_file_is_set_aside_and_not_saved_over()
    {
        File.WriteAllText(Sessions, "[ { \"Id\": \"a\", \"Name\": \"Alice\", trunc");

        Assert.Empty(SessionStore.Load(Sessions));
        SessionStore.Save(Sessions, []);   // what the app would do next

        var aside = Assert.Single(SetAside(Sessions));
        Assert.Equal("[ { \"Id\": \"a\", \"Name\": \"Alice\", trunc", File.ReadAllText(aside));
        Assert.Contains(JsonFile.TakeProblems(), p => p.Contains(aside));
    }

    [Fact]
    public void Null_entries_and_entries_missing_what_a_session_needs_are_dropped()
    {
        File.WriteAllText(Sessions, "[null]");
        Assert.Empty(SessionStore.Load(Sessions));

        File.WriteAllText(Sessions, """
            [
              null,
              { "Id": "a", "Name": "Alice", "Directory": "D:\\repo", "Command": "claude" },
              { "Id": "a", "Name": "Duplicate", "Directory": "D:\\repo", "Command": "claude" },
              { "Id": "", "Name": "No id", "Directory": "D:\\repo", "Command": "claude" },
              { "Id": "b", "Name": "No folder", "Directory": null, "Command": "claude" },
              { "Id": "c", "Name": null, "Directory": "D:\\other", "Command": "codex" }
            ]
            """);

        var loaded = SessionStore.Load(Sessions);

        Assert.Equal(new[] { "a", "c" }, loaded.Select(s => s.Id));
        Assert.Equal("", loaded[1].Name);
        Assert.Empty(SetAside(Sessions));   // a readable file with bad entries isn't damaged
    }

    [Fact]
    public void Saving_keeps_the_file_it_replaces_as_a_backup()
    {
        SessionStore.Save(Sessions, [Session("First")]);
        Assert.False(File.Exists(JsonFile.BackupPath(Sessions)));   // nothing to keep yet

        SessionStore.Save(Sessions, [Session("Second")]);

        Assert.Equal("Second", SessionStore.Load(Sessions).Single().Name);
        Assert.Equal("First", SessionStore.Load(JsonFile.BackupPath(Sessions)).Single().Name);
        Assert.False(File.Exists(Sessions + ".tmp"));
    }

    [Fact]
    public void The_backup_is_used_and_put_back_when_the_file_is_corrupt()
    {
        SessionStore.Save(Sessions, [Session("First")]);
        SessionStore.Save(Sessions, [Session("Second")]);
        File.WriteAllText(Sessions, "{ not json");

        Assert.Equal("First", SessionStore.Load(Sessions).Single().Name);

        Assert.Single(SetAside(Sessions));
        // Back in place, so the next start finds it even if nothing is saved in between.
        Assert.Equal("First", SessionStore.Load(Sessions).Single().Name);
    }

    [Fact]
    public void Only_the_newest_few_corrupt_copies_are_kept()
    {
        for (var i = 0; i < JsonFile.KeepCorrupt + 2; i++)
        {
            File.WriteAllText(Sessions, "broken " + i);
            SessionStore.Load(Sessions);
        }

        var kept = SetAside(Sessions).Select(File.ReadAllText).Order().ToList();

        Assert.Equal(Enumerable.Range(2, JsonFile.KeepCorrupt).Select(i => "broken " + i), kept);
    }

    [Fact]
    public void Projects_get_the_same_treatment()
    {
        var projects = Path.Combine(folder, "projects.json");
        File.WriteAllText(projects, """[ null, { "Id": "a", "Name": "FiscFlow" } ]""");
        Assert.Equal("FiscFlow", ProjectStore.Load(projects).Single().Name);

        ProjectStore.Save(projects, [new ProjectInfo { Id = "a", Name = "FiscFlow" }]);
        ProjectStore.Save(projects, [new ProjectInfo { Id = "b", Name = "Website" }]);
        File.WriteAllText(projects, "garbage");

        Assert.Equal("FiscFlow", ProjectStore.Load(projects).Single().Name);
        Assert.Single(SetAside(projects));
    }

    [Fact]
    public void Panels_with_null_entries_load()
    {
        var file = Path.Combine(folder, "panels.json");
        File.WriteAllText(file, "[null]");

        Assert.Empty(new PanelStore(file).Snapshot());
    }

    [Fact]
    public void Permission_files_with_null_entries_load()
    {
        var relay = Path.Combine(folder, "relay.json");
        File.WriteAllText(relay, """{ "paused": true, "rules": [null] }""");
        Assert.True(new RelayPolicy(relay).Paused);

        var control = Path.Combine(folder, "control.json");
        File.WriteAllText(control, "[null]");
        Assert.Empty(new ControlPolicy(control).Rules());

        var messages = Path.Combine(folder, "relay-messages.json");
        File.WriteAllText(messages, """{ "inboxes": { "s1": [null] }, "waiting": [null, { "from": "a", "to": "b", "messages": [null] }], "log": [null] }""");
        Assert.Empty(new RelayHub(new RelayPolicy(null), path: messages).Log());
    }

    [Fact]
    public void A_corrupt_permission_file_is_set_aside_and_asks_again_rather_than_using_the_older_copy()
    {
        var file = Path.Combine(folder, "plugin-grants.json");
        var allowed = new PluginGrants(file);
        allowed.Set("floor", "sessions", "allow", "H1");
        File.Copy(file, JsonFile.BackupPath(file));   // an older copy that still allows it
        File.WriteAllText(file, "{ damaged");

        Assert.Equal("ask", new PluginGrants(file).State("floor", "sessions"));
        Assert.Single(SetAside(file));
    }
}
