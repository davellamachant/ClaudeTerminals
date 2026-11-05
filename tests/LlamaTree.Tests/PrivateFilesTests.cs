using System.Runtime.InteropServices;
using LlamaTree.Platform;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>
/// The data folder and the sessions' token files (mcp-*.json) are only this user's on macOS and Linux: 0700 and 0600,
/// even under a umask that would give everyone read access, and ones made by an earlier version are narrowed.
/// </summary>
public sealed class PrivateFilesTests : IDisposable
{
    private const UnixFileMode Open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;   // 0755
    private const UnixFileMode Readable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;   // 0644

    private readonly string top = Path.Combine(Path.GetTempPath(), "lt-private-" + Guid.NewGuid().ToString("N")[..8]);

    public PrivateFilesTests() => Directory.CreateDirectory(top);

    public void Dispose()
    {
        try { Directory.Delete(top, true); } catch (IOException) { }
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint SetUmask(uint mask);

    /// <summary>Runs <paramref name="test"/> under umask 000, so nothing it makes is narrowed by the umask.</summary>
    private static void Permissive(Action test)
    {
        var old = SetUmask(0);
        try { test(); }
        finally { SetUmask(old); }
    }

    [UnixFact]
    public void A_new_folder_and_token_file_are_this_users_only_whatever_the_umask() => Permissive(() =>
    {
        var folder = Path.Combine(top, "LlamaTree", "panels");
        var file = Path.Combine(folder, "mcp-s1.json");

        PrivateFiles.CreateDirectory(folder);
        PrivateFiles.WriteAllText(file, "{\"token\":\"secret\"}");

        Assert.Equal(PrivateFiles.FolderMode, File.GetUnixFileMode(folder));
        Assert.Equal(PrivateFiles.FolderMode, File.GetUnixFileMode(Path.GetDirectoryName(folder)!));   // and the one it's in
        Assert.Equal(PrivateFiles.DataFileMode, File.GetUnixFileMode(file));
        Assert.Equal("{\"token\":\"secret\"}", File.ReadAllText(file));
    });

    [UnixFact]
    public void Ones_made_before_are_narrowed_when_written_again() => Permissive(() =>
    {
        var folder = Path.Combine(top, "panels");
        var file = Path.Combine(folder, "mcp-s1.json");
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, Open);
        File.WriteAllText(file, "an old, longer token file");
        File.SetUnixFileMode(file, Readable);

        PrivateFiles.CreateDirectory(folder);
        PrivateFiles.WriteAllText(file, "new");

        Assert.Equal(PrivateFiles.FolderMode, File.GetUnixFileMode(folder));
        Assert.Equal(PrivateFiles.DataFileMode, File.GetUnixFileMode(file));
        Assert.Equal("new", File.ReadAllText(file));
    });

    [UnixFact]
    public void At_start_the_folder_and_its_token_files_are_narrowed_and_nothing_else()
    {
        var panels = Path.Combine(top, "panels");
        Directory.CreateDirectory(panels);
        File.SetUnixFileMode(top, Open);
        File.SetUnixFileMode(panels, Open);
        var token = Path.Combine(panels, "mcp-s1.json");
        var other = Path.Combine(panels, "notes.txt");
        foreach (var f in new[] { token, other })
        {
            File.WriteAllText(f, "x");
            File.SetUnixFileMode(f, Readable);
        }

        PrivateFiles.Tighten(top);
        PrivateFiles.Tighten(panels, "mcp-*.json");
        PrivateFiles.Tighten(Path.Combine(top, "not-there"), "mcp-*.json");   // nothing to do, and no error

        Assert.Equal(PrivateFiles.FolderMode, File.GetUnixFileMode(top));
        Assert.Equal(PrivateFiles.FolderMode, File.GetUnixFileMode(panels));
        Assert.Equal(PrivateFiles.DataFileMode, File.GetUnixFileMode(token));
        Assert.Equal(Readable, File.GetUnixFileMode(other));
        Assert.False(Directory.Exists(Path.Combine(top, "not-there")));
    }

    [UnixFact]
    public void Saving_the_projects_makes_the_data_folder_this_users_only() => Permissive(() =>
    {
        var folder = Path.Combine(top, "LlamaTree");

        ProjectStore.Save(Path.Combine(folder, "projects.json"), []);

        Assert.Equal(PrivateFiles.FolderMode, File.GetUnixFileMode(folder));
    });

    [Fact]
    public void Folders_and_files_are_made_and_written_on_every_system()
    {
        var folder = Path.Combine(top, "a", "panels");
        var file = Path.Combine(folder, "mcp-s1.json");

        PrivateFiles.CreateDirectory(folder);
        PrivateFiles.CreateDirectory(folder);   // already there
        PrivateFiles.WriteAllText(file, "a longer first token");
        PrivateFiles.WriteAllText(file, "second");
        PrivateFiles.Tighten(folder, "mcp-*.json");

        Assert.True(Directory.Exists(folder));
        Assert.Equal("second", File.ReadAllText(file));
    }
}
