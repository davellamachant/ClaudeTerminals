using LlamaTree.Plugins;

namespace LlamaTree.Hosting;

/// <summary>
/// The window's copies of a server's files (see <see cref="HostLink.Remote"/>): files it was sent to open here, and its
/// plugins' folders, which the window serves from here. Kept in this computer's app data, a folder per server. What a
/// server sends is checked as it would be from anyone: names can't reach out of their folder; a file to open is taken
/// only when this computer asked to open one (<see cref="ExpectFile"/>); and the number and size of transfers, files
/// and plugins are capped, so a server can't fill this computer's disk or hold its files open. Called on the window's
/// (or this computer's host's) UI thread. What's refused is logged (see <see cref="AppLog"/>): why, never what was in it.
/// </summary>
internal sealed class ServerCopies(string folder, string server = "a server")
{
    private void Refused(string what) => AppLog.Warn($"{server}: {what}");

    private readonly Dictionary<string, Incoming> files = [];
    private readonly Dictionary<string, long> pluginBytes = [];

    /// <summary>The most a plugin's folder may hold (as the server checks too, see SessionHost).</summary>
    public const long MaxPluginBytes = 32L << 20;

    /// <summary>The most all of a server's plugins may hold here, and how many it may have.</summary>
    public const long MaxAllPluginBytes = 128L << 20;
    public const int MaxPlugins = 32;

    /// <summary>The most files a plugin, and all of a server's, may have here, however small (each takes a place on the disk).</summary>
    public const int MaxPluginFiles = 1000;
    public const int MaxAllPluginFiles = 4000;

    private readonly Dictionary<string, int> pluginFiles = [];

    /// <summary>Files on their way at once: to open, and of plugins.</summary>
    private const int MaxOpenTransfers = 4;

    /// <summary>The longest part of a file in a message: <see cref="ServerFiles.PartSize"/> bytes, as base64.</summary>
    private const int MaxPartChars = (ServerFiles.PartSize + 2) / 3 * 4;

    /// <summary>How long an asked-for file may take to start coming.</summary>
    private static readonly TimeSpan ExpectFor = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Each file this computer is still waiting for (oldest first): when it asked, and the name of what it asked to open
    /// (made safe), or null when the request didn't name a path.
    /// </summary>
    private readonly List<(DateTime At, string? Name)> expected = [];
    private bool tidied;

    private sealed record Incoming(FileStream Stream, string Path)
    {
        public int Next { get; set; }
    }

    /// <summary>The folder a server's plugin is served from here.</summary>
    public string PluginFolder(string id) => Path.Combine(folder, "plugins", id);

    /// <summary>
    /// This computer has asked the server to open something (a file, a path link…): a file may come for it, starting
    /// within <see cref="ExpectFor"/>. A file the server sends unasked is refused. <paramref name="path"/> is the path the
    /// request named, as sent to the server: the file that comes for it must have its name (see <see cref="FilePart"/>).
    /// </summary>
    public void ExpectFile(string? path = null)
    {
        Prune();
        if (expected.Count < 16) expected.Add((DateTime.UtcNow, string.IsNullOrWhiteSpace(path) ? null : ServerFiles.SafeName(path)));
    }

    private void Prune() => expected.RemoveAll(e => DateTime.UtcNow - e.At > ExpectFor);

    /// <summary>
    /// A part of a file to open. Returns its path here once the last part has come, else null. A file nobody asked
    /// for, one whose name isn't that of a file asked for, one past the number on their way at once, one whose parts
    /// come out of order, and one that grows too big are dropped. The name it's kept under is the one asked for (made
    /// safe), as the file system really made it; and on Windows it's marked as having come from the internet, so
    /// Windows and the apps that open it treat it as they would a download.
    /// </summary>
    public string? FilePart(string token, string name, string data, int index, bool last)
    {
        if (token.Length is 0 or > 64 || !token.All(char.IsAsciiLetterOrDigit) || token.Contains('/'))
        {
            if (index == 0) Refused("a file to open was refused: its token isn't one.");
            return null;
        }
        if (index == 0)
        {
            Drop(token);
            Prune();
            if (files.Keys.Count(k => !k.Contains('/')) >= MaxOpenTransfers)
            {
                Refused($"a file to open was refused: {MaxOpenTransfers} were already on their way.");
                return null;
            }
            // The oldest request for a file of this name; else the oldest that named none.
            var safe = ServerFiles.SafeName(name);
            var asked = expected.FindIndex(e => e.Name == safe);
            if (asked < 0) asked = expected.FindIndex(e => e.Name == null);
            if (asked < 0)
            {
                Refused(expected.Count == 0
                    ? "a file to open was refused: nothing here asked to open one."
                    : "a file to open was refused: nothing here asked to open a file of that name.");
                return null;
            }
            safe = expected[asked].Name ?? safe;
            expected.RemoveAt(asked);
            Tidy();
            var dir = Path.Combine(folder, "opened", token);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, safe);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            // What's opened, and judged by its name, is the file the system made, whatever the name given said.
            if (OperatingSystem.IsWindows() && Terminal.Native.FinalPathOf(stream.SafeFileHandle) is { } made)
                path = Path.Combine(dir, Path.GetFileName(made));
            files[token] = new Incoming(stream, path);
        }
        var done = Append(token, data, index, last, ServerFiles.MaxFile, out _);
        if (done != null)
        {
            MarkFromInternet(done);
            ReadOnly(done);
        }
        return done;
    }

    /// <summary>
    /// Windows: the file is marked as from the internet (the Mark of the Web, a Zone.Identifier stream with ZoneId 3), as
    /// a browser marks a download: SmartScreen checks it before it runs, and Office and others open it protected. A
    /// file system without such streams (FAT, some network drives) can't be marked: the copy is kept all the same.
    /// </summary>
    internal static void MarkFromInternet(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { }
    }

    /// <summary>A plugin's copy starts afresh (its folder here is emptied). Past <see cref="MaxPlugins"/>, refused.</summary>
    public void PluginBegin(string id)
    {
        if (!PluginCatalog.IsValidId(id)) return;
        if (!pluginBytes.ContainsKey(id) && pluginBytes.Count >= MaxPlugins)
        {
            Refused($"plugin {id} was refused: it has {MaxPlugins} here already.");
            return;
        }
        PluginGone(id);
        pluginBytes[id] = 0;
    }

    /// <summary>A plugin's copy is dropped: the server no longer has it, or it was more than allowed.</summary>
    public void PluginGone(string id)
    {
        if (!PluginCatalog.IsValidId(id)) return;
        foreach (var key in files.Keys.Where(k => k.StartsWith(id + "/", StringComparison.Ordinal)).ToList()) Drop(key);
        pluginBytes.Remove(id);
        pluginFiles.Remove(id);
        try { Directory.Delete(PluginFolder(id), true); } catch (DirectoryNotFoundException) { } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Every plugin's copy but <paramref name="keep"/>'s is dropped, on the disk too: those from an earlier run (whose
    /// sizes this one never counted) and any half sent. Done as the server's host says who it is, before it sends its
    /// plugins again, so the disk only ever holds what's counted against the limits above.
    /// </summary>
    public void ClearPluginsExcept(IReadOnlySet<string> keep)
    {
        foreach (var id in pluginBytes.Keys.Where(id => !keep.Contains(id)).ToList()) PluginGone(id);
        var plugins = new DirectoryInfo(Path.Combine(folder, "plugins"));
        if (!plugins.Exists) return;
        try
        {
            foreach (var entry in plugins.GetFileSystemInfos())
            {
                if (keep.Contains(entry.Name)) continue;
                try
                {
                    if (entry is DirectoryInfo d) d.Delete(true);
                    else entry.Delete();
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A part of one of a plugin's files, at <paramref name="relative"/> in its folder ('/' between folders).</summary>
    public void PluginPart(string id, string relative, string data, int index, bool last)
    {
        if (!PluginCatalog.IsValidId(id) || !pluginBytes.ContainsKey(id)) return;
        var parts = relative.Split('/');
        var invalid = Path.GetInvalidFileNameChars();
        if (relative.Length > 400 || parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.Any(c => invalid.Contains(c) || c is '\\' or ':' || char.IsControl(c))))   // the same on every system
        {
            if (index == 0) Refused($"a file of plugin {id} was refused: its name can't be used.");
            return;
        }
        var root = PluginFolder(id);
        var path = Path.GetFullPath(Path.Combine([root, .. parts]));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            if (index == 0) Refused($"a file of plugin {id} was refused: it's outside the plugin's folder.");
            return;
        }
        var key = id + "/" + relative;
        if (index == 0)
        {
            Drop(key);
            if (files.Keys.Count(k => k.Contains('/')) >= MaxOpenTransfers)
            {
                Refused($"a file of plugin {id} was refused: {MaxOpenTransfers} were already on their way.");
                return;
            }
            // Counted as it starts, empty or not: too many files is none of the plugin.
            pluginFiles[id] = pluginFiles.GetValueOrDefault(id) + 1;
            if (pluginFiles[id] > MaxPluginFiles || pluginFiles.Values.Sum() > MaxAllPluginFiles)
            {
                Refused($"plugin {id} was dropped: it has more files than a plugin may ({MaxPluginFiles}, {MaxAllPluginFiles} in all).");
                PluginGone(id);
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                files[key] = new Incoming(new FileStream(path, FileMode.Create, FileAccess.Write), path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file where it needs a folder, or a folder where it needs a file: that file isn't taken.
                Refused($"a file of plugin {id} was refused: it collides with another of its files or folders ({ex.GetType().Name}).");
                return;
            }
        }
        Append(key, data, index, last, MaxPluginBytes, out var written);
        pluginBytes[id] += written;
        // More than a plugin, or all of a server's, should be: none of it.
        if (pluginBytes[id] > MaxPluginBytes || pluginBytes.Values.Sum() > MaxAllPluginBytes)
        {
            Refused($"plugin {id} was dropped: it's bigger than a plugin may be ({MaxPluginBytes >> 20} MB, {MaxAllPluginBytes >> 20} MB in all).");
            PluginGone(id);
        }
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
            Refused($"a file on its way was dropped: {(ex is InvalidDataException ? "a part out of order, or more than it may be" : ex.GetType().Name)}.");
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
