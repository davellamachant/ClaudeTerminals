using LlamaTree.Plugins;

namespace LlamaTree.Hosting;

/// <summary>
/// The window's copies of a server's files (see <see cref="HostLink.Remote"/>): files it was sent to open here, and its
/// plugins' folders, which the window serves from here. Kept in this computer's app data, a folder per server. What a
/// server sends is checked as it would be from anyone: names can't reach out of their folder; a file to open is taken
/// only when this computer asked to open one (<see cref="ExpectFile"/>); and the number and size of transfers, files
/// and plugins are capped, so a server can't fill this computer's disk or hold its files open. Called on the window's
/// (or this computer's host's) UI thread.
/// </summary>
internal sealed class ServerCopies(string folder)
{
    private readonly Dictionary<string, Incoming> files = [];
    private readonly Dictionary<string, long> pluginBytes = [];

    /// <summary>The most a plugin's folder may hold (as the server checks too, see SessionHost).</summary>
    public const long MaxPluginBytes = 32L << 20;

    /// <summary>The most all of a server's plugins may hold here, and how many it may have.</summary>
    public const long MaxAllPluginBytes = 128L << 20;
    public const int MaxPlugins = 32;

    /// <summary>Files on their way at once: to open, and of plugins.</summary>
    private const int MaxOpenTransfers = 4;

    /// <summary>The longest part of a file in a message: <see cref="ServerFiles.PartSize"/> bytes, as base64.</summary>
    private const int MaxPartChars = (ServerFiles.PartSize + 2) / 3 * 4;

    /// <summary>How long an asked-for file may take to start coming.</summary>
    private static readonly TimeSpan ExpectFor = TimeSpan.FromMinutes(2);

    /// <summary>When this computer asked to open each file it's still waiting for (oldest first).</summary>
    private readonly Queue<DateTime> expected = new();
    private bool tidied;

    private sealed record Incoming(FileStream Stream, string Path)
    {
        public int Next { get; set; }
    }

    /// <summary>The folder a server's plugin is served from here.</summary>
    public string PluginFolder(string id) => Path.Combine(folder, "plugins", id);

    /// <summary>
    /// This computer has asked the server to open something (a file, a path link…): a file may come for it, starting
    /// within <see cref="ExpectFor"/>. A file the server sends unasked is refused.
    /// </summary>
    public void ExpectFile()
    {
        Prune();
        if (expected.Count < 16) expected.Enqueue(DateTime.UtcNow);
    }

    private void Prune()
    {
        while (expected.Count > 0 && DateTime.UtcNow - expected.Peek() > ExpectFor) expected.Dequeue();
    }

    /// <summary>
    /// A part of a file to open. Returns its path here once the last part has come, else null. A file nobody asked
    /// for, one past the number on their way at once, one whose parts come out of order, and one that grows too big
    /// are dropped.
    /// </summary>
    public string? FilePart(string token, string name, string data, int index, bool last)
    {
        if (token.Length is 0 or > 64 || !token.All(char.IsAsciiLetterOrDigit) || token.Contains('/')) return null;
        if (index == 0)
        {
            Drop(token);
            Prune();
            if (expected.Count == 0 || files.Keys.Count(k => !k.Contains('/')) >= MaxOpenTransfers) return null;
            expected.Dequeue();
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

    /// <summary>A plugin's copy starts afresh (its folder here is emptied). Past <see cref="MaxPlugins"/>, refused.</summary>
    public void PluginBegin(string id)
    {
        if (!PluginCatalog.IsValidId(id)) return;
        if (!pluginBytes.ContainsKey(id) && pluginBytes.Count >= MaxPlugins) return;
        PluginGone(id);
        pluginBytes[id] = 0;
    }

    /// <summary>A plugin's copy is dropped: the server no longer has it, or it was more than allowed.</summary>
    public void PluginGone(string id)
    {
        if (!PluginCatalog.IsValidId(id)) return;
        foreach (var key in files.Keys.Where(k => k.StartsWith(id + "/", StringComparison.Ordinal)).ToList()) Drop(key);
        pluginBytes.Remove(id);
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
            if (files.Keys.Count(k => k.Contains('/')) >= MaxOpenTransfers) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            files[key] = new Incoming(new FileStream(path, FileMode.Create, FileAccess.Write), path);
        }
        Append(key, data, index, last, MaxPluginBytes, out var written);
        pluginBytes[id] += written;
        // More than a plugin, or all of a server's, should be: none of it.
        if (pluginBytes[id] > MaxPluginBytes || pluginBytes.Values.Sum() > MaxAllPluginBytes) PluginGone(id);
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
            if (index != f.Next++ || data.Length > MaxPartChars) throw new InvalidDataException();
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
