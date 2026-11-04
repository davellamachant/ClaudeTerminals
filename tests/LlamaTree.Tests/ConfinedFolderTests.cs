using LlamaTree.Platform;
using LlamaTree.Terminal;

namespace LlamaTree.Tests;

/// <summary>
/// A session another session's agent started is held to its folder as its process starts: checked and started in the
/// same open folder, so renaming a folder on the way and putting a link in its place can't move it elsewhere.
/// </summary>
public class ConfinedFolderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lt-confine-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string allowed, child, outside;

    public ConfinedFolderTests()
    {
        allowed = Path.Combine(root, "project");
        child = Path.Combine(allowed, "src", "api");
        outside = Path.Combine(root, "private");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(outside);
        allowed = PathText.ResolvedPath(allowed)!;   // as it's stored when allowed (macOS: /private/var/...)
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_folder_inside_is_held_and_one_outside_refused()
    {
        using (var f = ConfinedFolder.Open(child, allowed))
            Assert.Equal(PathText.ResolvedPath(child), f.Path);
        Assert.Throws<IOException>(() => ConfinedFolder.Open(outside, allowed));
        Assert.Throws<IOException>(() => ConfinedFolder.Open(Path.Combine(root, "missing"), allowed));
    }

    [Fact]
    public void The_root_kept_is_the_one_checked_and_a_missing_root_confines_nothing()
    {
        // Resolved once, and the folder checked against that very value, which is what's kept.
        var confined = PathText.Confine(child, Path.Combine(root, "project"));
        Assert.NotNull(confined);
        Assert.Equal(allowed, confined.Value.Root);
        Assert.Equal(PathText.ResolvedPath(child), confined.Value.Folder);
        // A root that has gone (renamed, deleted) gives no confinement at all: refused, never "unconfined".
        Assert.Null(PathText.Confine(child, Path.Combine(root, "gone")));
        // A root swapped for a link elsewhere: what it leads to now is the root, and the folder isn't inside it.
        var swapped = Path.Combine(root, "swapped");
        try { Directory.CreateSymbolicLink(swapped, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        Assert.Null(PathText.Confine(child, swapped));
    }

    [Fact]
    public void A_session_an_agent_started_without_its_folder_held_isnt_started()
    {
        // Saved by a build between (before every such session was held to its folder).
        var s = new LlamaTree.Sessions.TerminalSession(new LlamaTree.Sessions.SessionInfo
        {
            Name = "old helper", Directory = child, Command = OperatingSystem.IsWindows() ? "cmd" : "sh", StartedBy = "lead", Within = null,
        });
        Assert.Throws<IOException>(() => s.Start(80, 24));
        Assert.False(s.IsRunning);
    }

    [Fact]
    public void A_link_inside_that_leads_out_is_refused()
    {
        var link = Path.Combine(allowed, "out");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // Windows without the right to make links
        Assert.Throws<IOException>(() => ConfinedFolder.Open(link, allowed));
    }

    [Fact]
    public void While_held_on_Windows_no_folder_on_its_way_can_be_renamed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var src = Path.Combine(allowed, "src");
        using (ConfinedFolder.Open(child, allowed))
        {
            Assert.ThrowsAny<IOException>(() => Directory.Move(child, child + "-moved"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(src, src + "-moved"));     // a folder above it
            Assert.ThrowsAny<IOException>(() => Directory.Move(allowed, allowed + "-moved"));
        }
        // Let go once the process has started: they can be renamed again.
        Directory.Move(src, src + "-moved");
        Directory.Move(src + "-moved", src);
    }

    [Fact]
    public void On_macOS_and_Linux_the_open_folder_stays_the_one_checked_when_a_link_takes_its_place()
    {
        if (OperatingSystem.IsWindows()) return;
        var src = Path.Combine(allowed, "src");
        using var f = ConfinedFolder.Open(child, allowed);
        // The swap: the folder above moved away, and a link to somewhere else put where it was.
        Directory.Move(src, src + "-moved");
        Directory.CreateSymbolicLink(src, outside);
        Directory.CreateDirectory(Path.Combine(outside, "api"));
        // The open folder is still the one checked (now under src-moved), not what the path leads to now.
        Assert.Equal(Path.Combine(allowed, "src-moved", "api"), Libc.Instance.PathOf(f.Descriptor));
        // And a fresh check of the path, as at the next start, is refused.
        Assert.Throws<IOException>(() => ConfinedFolder.Open(child, allowed));
    }

    [Fact]
    public async Task On_macOS_and_Linux_a_confined_process_starts_in_the_checked_folder()
    {
        if (OperatingSystem.IsWindows()) return;
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        using var p = new UnixPtyProcess();
        var output = new System.Text.StringBuilder();
        var exited = new TaskCompletionSource();
        p.Output += s => { lock (output) output.Append(s); };
        p.Exited += _ => exited.TrySetResult();
        p.Start("pwd -P", child, env, 80, 24, allowed);
        await Task.WhenAny(exited.Task, Task.Delay(10000));
        lock (output) Assert.Contains(PathText.ResolvedPath(child)!, output.ToString());
        Assert.Throws<IOException>(() => new UnixPtyProcess().Start("pwd", outside, env, 80, 24, allowed));
    }
}
