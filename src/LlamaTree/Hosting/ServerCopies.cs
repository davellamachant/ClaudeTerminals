using LlamaTree.Platform;
using LlamaTree.Plugins;

namespace LlamaTree.Hosting;

/// <summary>
/// The window's copies of a server's files (see <see cref="HostLink.Remote"/>): files it was sent to open here, and its
/// plugins' folders, which the window serves from here. Kept in this computer's app data, a folder per server. What a
/// server sends is checked as it would be from anyone: names can't reach out of their folder; a file to open is taken
/// only when this computer asked to open one (<see cref="ExpectFile"/>); and the number and size of transfers, files
/// and plugins are capped, so a server can't fill this computer's disk or hold its files open. Called on the window's
/// (or this computer's host's) UI thread. <paramref name="server"/> is how the server is reached (user@server, or a Host
/// from the SSH config), recorded on Linux as where a file to open came from (see <see cref="MarkFromInternet"/>).
/// </summary>
internal sealed class ServerCopies(string folder, string? server = null)
{
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
    /// safe), as the file system really made it; and it's marked as having come from the internet (see
    /// <see cref="MarkFromInternet"/>), so the system and the apps that open it treat it as they would a download.
    /// </summary>
    public string? FilePart(string token, string name, string data, int index, bool last)
    {
        if (token.Length is 0 or > 64 || !token.All(char.IsAsciiLetterOrDigit) || token.Contains('/')) return null;
        if (index == 0)
        {
            Drop(token);
            Prune();
            if (files.Keys.Count(k => !k.Contains('/')) >= MaxOpenTransfers) return null;
            // The oldest request for a file of this name; else the oldest that named none.
            var safe = ServerFiles.SafeName(name);
            var asked = expected.FindIndex(e => e.Name == safe);
            if (asked < 0) asked = expected.FindIndex(e => e.Name == null);
            if (asked < 0) return null;
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
            MarkFromInternet(done, OriginUrl(server, Path.GetFileName(done)));
            ReadOnly(done);
        }
        return done;
    }

    /// <summary>
    /// Where a file to open came from, as Linux records it: llamatree-server://&lt;server&gt;/&lt;name&gt;, the server as
    /// it's reached without the user name (user@server); one that isn't a host name is kept, escaped, in the path instead.
    /// </summary>
    internal static string OriginUrl(string? server, string name)
    {
        var host = server?[(server.LastIndexOf('@') + 1)..] ?? "";
        return Uri.CheckHostName(host) != UriHostNameType.Unknown
            ? $"llamatree-server://{host}/{Uri.EscapeDataString(name)}"
            : $"llamatree-server:///{Uri.EscapeDataString(host.Length > 0 ? host : "server")}/{Uri.EscapeDataString(name)}";
    }

    /// <summary>
    /// The file is marked as from the internet, as a browser marks a download, while it can still be written (Linux
    /// lets only those who may write a file set its user attributes). Where it can't be marked (a file system without the place for it: FAT,
    /// some network drives, tmpfs...), the copy is kept all the same.
    /// <list type="bullet">
    /// <item>Windows: the Mark of the Web, a Zone.Identifier stream with ZoneId 3. SmartScreen checks it before it runs,
    /// and Office and others open it protected.</item>
    /// <item>macOS: the com.apple.quarantine attribute (see <see cref="QuarantineValue"/>). Gatekeeper checks it before
    /// it runs, and asks before it's first opened.</item>
    /// <item>Linux: user.xdg.origin.url, <paramref name="origin"/> (freedesktop.org's attribute for where a file was
    /// downloaded from). Nothing enforces it, but it's there for file managers and the person to see.</item>
    /// </list>
    /// </summary>
    internal static void MarkFromInternet(string path, string? origin = null)
    {
        try
        {
            if (OperatingSystem.IsWindows()) File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            else if (OperatingSystem.IsMacOS()) ExtendedAttributes.TrySet(path, "com.apple.quarantine", QuarantineValue(DateTimeOffset.UtcNow));
            else if (origin != null) ExtendedAttributes.TrySet(path, "user.xdg.origin.url", origin);
        }
        catch (Exception) { /* never a reason to lose the copy */ }
    }

    /// <summary>
    /// macOS's quarantine value, "flags;time;agent;event". Flags 0081: 0x0080, still in quarantine (macOS adds 0x0040
    /// once the person has approved it), and 0x0001, downloaded (QTN_FLAG_DOWNLOAD); not 0x0002 (QTN_FLAG_SANDBOX, written
    /// by a sandboxed app), which this isn't. The time as hex seconds since 1970; this app as the agent; and no event id,
    /// as nothing is added to the system's QuarantineEvents database.
    /// </summary>
    internal static string QuarantineValue(DateTimeOffset at) => $"0081;{at.ToUnixTimeSeconds():x8};LlamaTree;";

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
        pluginFiles.Remove(id);
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
            // Counted as it starts, empty or not: too many files is none of the plugin.
            pluginFiles[id] = pluginFiles.GetValueOrDefault(id) + 1;
            if (pluginFiles[id] > MaxPluginFiles || pluginFiles.Values.Sum() > MaxAllPluginFiles)
            {
                PluginGone(id);
                return;
            }
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
