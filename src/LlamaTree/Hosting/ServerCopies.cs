using LlamaTree.Plugins;

namespace LlamaTree.Hosting;

/// <summary>
/// The window's copies of a server's files (see <see cref="HostLink.Remote"/>): files it was sent to open here, and its
/// plugins' folders, which the window serves from here. Kept in this computer's app data, a folder per server. What a
/// server sends is checked as it would be from anyone: names can't reach out of their folder, and sizes are capped.
/// Called on the window's UI thread.
/// </summary>
internal sealed class ServerCopies(string folder)
{
    private readonly Dictionary<string, Incoming> files = [];
    private readonly Dictionary<string, long> pluginBytes = [];

    /// <summary>The most a plugin's folder may hold (as the server checks too, see SessionHost).</summary>
    public const long MaxPluginBytes = 32L << 20;
    private bool tidied;

    private sealed record Incoming(FileStream Stream, string Path)
    {
        public int Next { get; set; }
    }

    /// <summary>The folder a server's plugin is served from here.</summary>
    public string PluginFolder(string id) => Path.Combine(folder, "plugins", id);

    /// <summary>
    /// A part of a file to open. Returns its path here once the last part has come, else null. A file whose parts come
    /// out of order, or that grows too big, is dropped.
    /// </summary>
    public string? FilePart(string token, string name, string data, int index, bool last)
    {
        if (token.Length is 0 or > 64 || !token.All(char.IsAsciiLetterOrDigit)) return null;
        if (index == 0)
        {
            Drop(token);
            Tidy();
            var dir = Path.Combine(folder, "opened", token);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ServerFiles.SafeName(name));
            files[token] = new Incoming(new FileStream(path, FileMode.Create, FileAccess.Write), path);
        }
        var done = Append(token, data, index, last, ServerFiles.MaxFile, out _);
        if (done != null) ReadOnly(done);
        return done;
    }

    /// <summary>A plugin's copy starts afresh (its folder here is emptied).</summary>
    public void PluginBegin(string id)
    {
        if (!PluginCatalog.IsValidId(id)) return;
        foreach (var key in files.Keys.Where(k => k.StartsWith(id + "/", StringComparison.Ordinal)).ToList()) Drop(key);
        pluginBytes[id] = 0;
        try { Directory.Delete(PluginFolder(id), true); } catch (DirectoryNotFoundException) { } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A part of one of a plugin's files, at <paramref name="relative"/> in its folder ('/' between folders).</summary>
    public void PluginPart(string id, string relative, string data, int index, bool last)
    {
        if (!PluginCatalog.IsValidId(id) || !pluginBytes.ContainsKey(id)) return;
        var parts = relative.Split('/');
        var invalid = Path.GetInvalidFileNameChars();
        if (relative.Length > 400 || parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.Any(c => invalid.Contains(c) || c is '\\' or ':' || char.IsControl(c)))) return;   // the same on every system
        var root = PluginFolder(id);
        var path = Path.GetFullPath(Path.Combine([root, .. parts]));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        var key = id + "/" + relative;
        if (index == 0)
        {
            Drop(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            files[key] = new Incoming(new FileStream(path, FileMode.Create, FileAccess.Write), path);
        }
        Append(key, data, index, last, MaxPluginBytes, out var written);
        pluginBytes[id] += written;
        if (pluginBytes[id] > MaxPluginBytes) PluginBegin(id);   // more than a plugin should be: none of it
    }

    /// <summary>
    /// A copy of a server's file is made read-only: it's a copy, and saving it wouldn't change the file on the server, so
    /// an editor says so (or asks where to save) rather than appearing to save it.
    /// </summary>
    private static void ReadOnly(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string? Append(string key, string data, int index, bool last, long max, out int written)
    {
        written = 0;
        if (!files.TryGetValue(key, out var f)) return null;
        try
        {
            if (index != f.Next++) throw new InvalidDataException();
            var bytes = Convert.FromBase64String(data);
            if (f.Stream.Length + bytes.Length > max) throw new InvalidDataException();
            f.Stream.Write(bytes);
            written = bytes.Length;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException)
        {
            Drop(key);
            return null;
        }
        if (!last) return null;
        files.Remove(key);
        f.Stream.Dispose();
        return f.Path;
    }

    private void Drop(string key)
    {
        if (!files.Remove(key, out var f)) return;
        f.Stream.Dispose();
        try { File.Delete(f.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The connection ended: files half sent are dropped.</summary>
    public void DropAll()
    {
        foreach (var key in files.Keys.ToList()) Drop(key);
    }

    /// <summary>Files opened a day ago or more are cleared out (once a run, before the first is saved).</summary>
    private void Tidy()
    {
        if (tidied) return;
        tidied = true;
        var opened = new DirectoryInfo(Path.Combine(folder, "opened"));
        if (!opened.Exists) return;
        foreach (var d in opened.EnumerateDirectories().Where(d => d.CreationTimeUtc < DateTime.UtcNow.AddDays(-1)))
        {
            try
            {
                foreach (var f in d.EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;   // Windows won't delete a read-only file
                d.Delete(true);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
