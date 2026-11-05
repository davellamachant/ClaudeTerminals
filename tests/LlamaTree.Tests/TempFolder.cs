namespace LlamaTree.Tests;

/// <summary>
/// A folder of the test's own under the temp folder, deleted afterwards. A store makes the folder it saves in private
/// (0700, see PrivateFiles), which an ordinary user can't do to Linux's shared /tmp: saved straight into the temp folder,
/// its file is never written there, so a test that reads it back finds nothing (macOS's temp folder is the user's own).
/// </summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lt-" + Guid.NewGuid().ToString("N")[..12]);

    public TempFolder() => Directory.CreateDirectory(Path);

    /// <summary>A file's path in the folder.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
