using LlamaTree.Sessions;

namespace LlamaTree.Hosting;

/// <summary>
/// The files of the computer the sessions run on, for a window on another computer (see <see cref="HostLink.Remote"/>):
/// its folders to browse, files pasted into its sessions (copied up from the window's computer), and the pieces files
/// travel in over the connection, either way.
/// </summary>
internal static class ServerFiles
{
    /// <summary>The bytes in one message: small enough that a session's output is never held up long behind a file.</summary>
    public const int PartSize = 192 * 1024;

    /// <summary>The biggest file sent to a window to open, or pasted into a session.</summary>
    public const long MaxFile = 200L << 20;

    /// <summary>Most entries a folder lists: past this, it says there are more.</summary>
    private const int MaxEntries = 5000;

    /// <summary>
    /// A folder's contents: its subfolders, then its files, by name. An empty path (or ~) is the home folder, and ~/
    /// starts from it.
    /// </summary>
    public static object List(string? path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var full = Resolve(path, home);
        if (!Directory.Exists(full)) throw new InvalidOperationException($"There's no folder {full} on {Environment.MachineName}.");
        var entries = new List<Entry>();
        var more = false;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        foreach (var e in new DirectoryInfo(full).EnumerateFileSystemInfos("*", options))
        {
            if (entries.Count == MaxEntries) { more = true; break; }
            var folder = e is DirectoryInfo;
            var hidden = e.Name.StartsWith('.') || (OperatingSystem.IsWindows() && e.Attributes.HasFlag(FileAttributes.Hidden));
            long? size = null;
            if (!folder) try { size = ((FileInfo)e).Length; } catch (IOException) { }
            entries.Add(new Entry(e.Name, folder, hidden, size));
        }
        entries.Sort((a, b) => a.Folder != b.Folder ? (a.Folder ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new { path = full, parent = Path.GetDirectoryName(full), home, separator = Path.DirectorySeparatorChar.ToString(), entries, more };
    }

    private sealed record Entry(string Name, bool Folder, bool Hidden, long? Size);

    /// <summary>A path typed or browsed to, made full: ~ is the home folder, and a relative path starts there too.</summary>
    public static string Resolve(string? path, string home)
    {
        path = path?.Trim() ?? "";
        if (path.Length == 0 || path == "~") return home;
        if (path.StartsWith("~/") || path.StartsWith("~\\")) path = Path.Combine(home, path[2..]);
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(home, path));
    }

    /// <summary>
    /// A name from the other computer, made safe to use as a file name here: no folders, nothing the file system refuses,
    /// and the same rule on every system. Windows drops dots and spaces from the end of a name as it makes the file, so
    /// "run.cmd ." would be created as run.cmd while the name kept still read ".cmd ": they're stripped here, all of them
    /// in any order, so the name kept is the file made. Invisible characters that reorder text (a right-to-left override
    /// can show "evil‮txt.exe" as "evilexe.txt") are replaced, and a device name (CON, NUL, COM1...) gets a _ in front.
    /// </summary>
    public static string SafeName(string? name)
    {
        var leaf = (name ?? "").Replace('\\', '/').Split('/')[^1];
        var invalid = Path.GetInvalidFileNameChars();
        leaf = new string(leaf.Select(c => invalid.Contains(c) || c == ':' || char.IsControl(c) ||
            char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? '_' : c).ToArray());
        if (leaf.Length > 120) leaf = leaf[..80] + leaf[^40..];
        leaf = leaf.TrimStart();
        var end = leaf.Length;
        while (end > 0 && (leaf[end - 1] == '.' || char.IsWhiteSpace(leaf[end - 1]))) end--;
        leaf = leaf[..end];
        if (leaf.Length == 0) return "file";
        return IsDeviceName(leaf) ? "_" + leaf : leaf;
    }

    /// <summary>
    /// Whether Windows takes the name for a device rather than a file (CON, PRN, AUX, NUL, COM1-9, LPT1-9, with any
    /// extension, as Windows reads them; superscript digits count too).
    /// </summary>
    private static bool IsDeviceName(string leaf)
    {
        var stem = leaf.Split('.')[0].TrimEnd();
        if (stem.Length == 3) return stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL";
        return stem.Length == 4 && stem[..3].ToUpperInvariant() is "COM" or "LPT" && stem[3] is >= '0' and <= '9' or '¹' or '²' or '³';
    }

    /// <summary>
    /// Copies files from this computer to a server, a part at a time, for pasting into a session there: their paths
    /// there. Every few parts it waits for the server's host to have written the ones before, so a big file isn't all
    /// held in memory waiting to go. <paramref name="request"/> sends a request and waits for its reply;
    /// <paramref name="send"/> sends a page message (JSON) without waiting.
    /// </summary>
    public static async Task<List<string>> UploadAsync(string[] paths, Func<object, Task<System.Text.Json.JsonElement>> request, Action<string> send)
    {
        var copied = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) throw new InvalidOperationException($"{Path.GetFileName(path)} is a folder: only files can be pasted into a session on a server.");
            var size = new FileInfo(path).Length;
            if (size > MaxFile) throw new InvalidOperationException($"{Path.GetFileName(path)} is too big to paste ({size >> 20} MB; at most {MaxFile >> 20} MB).");
        }
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        foreach (var path in paths)
        {
            var token = Guid.NewGuid().ToString("N");
            await request(new { type = "upload.begin", token, name = Path.GetFileName(path) });
            await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, useAsync: true);
            var buffer = new byte[PartSize];
            Task? written = null;
            for (var part = 1; ; part++)
            {
                var n = await f.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
                if (n == 0) break;
                var data = Convert.ToBase64String(buffer, 0, n);
                if (part % 8 != 0) send(System.Text.Json.JsonSerializer.Serialize(new { type = "upload.data", token, data }, json));
                else
                {
                    if (written != null) await written;
                    written = request(new { type = "upload.data", token, data });
                }
                if (n < buffer.Length) break;
            }
            if (written != null) await written;
            copied.Add((await request(new { type = "upload.end", token })).GetString() ?? throw new InvalidOperationException("The server didn't say where the file went."));
        }
        return copied;
    }

    /// <summary>
    /// Where files pasted into this computer's sessions from another are kept: the app's data folder, so only you can
    /// read them. Each paste has a folder of its own, so files keep their names; ones a week old are cleared out.
    /// </summary>
    public static string UploadFolder => Path.Combine(SessionStore.AppDataFolder, "pasted");

    /// <summary>
    /// Files on their way here from the window's computer: each begun, sent in parts, then ended, which gives its path.
    /// Called on the app thread.
    /// </summary>
    public sealed class Uploads(string folder)
    {
        private readonly Dictionary<string, (FileStream File, string Path)> open = [];
        private string? batch;   // this paste's folder, until it's a minute old

        public void Begin(string token, string name)
        {
            if (open.Count > 64 || token.Length is 0 or > 64) throw new InvalidOperationException("Too many files at once.");
            Abandon(token);
            var into = BatchFolder();
            var safe = SafeName(name);
            var path = Path.Combine(into, safe);
            for (var i = 2; File.Exists(path); i++)
                path = Path.Combine(into, $"{Path.GetFileNameWithoutExtension(safe)} ({i}){Path.GetExtension(safe)}");
            open[token] = (new FileStream(path, FileMode.CreateNew, FileAccess.Write), path);
        }

        public void Write(string token, string base64)
        {
            if (!open.TryGetValue(token, out var f)) return;   // given up on
            var bytes = Convert.FromBase64String(base64);
            if (f.File.Length + bytes.Length > MaxFile)
            {
                Abandon(token);
                throw new InvalidOperationException($"{Path.GetFileName(f.Path)} is too big to paste (more than {MaxFile >> 20} MB).");
            }
            f.File.Write(bytes);
        }

        /// <summary>The file has all come: its path here.</summary>
        public string End(string token)
        {
            if (!open.Remove(token, out var f)) throw new InvalidOperationException("That file didn't arrive.");
            f.File.Dispose();
            return f.Path;
        }

        /// <summary>The window has gone: files half sent are deleted.</summary>
        public void AbandonAll()
        {
            foreach (var t in open.Keys.ToList()) Abandon(t);
        }

        private void Abandon(string token)
        {
            if (!open.Remove(token, out var f)) return;
            f.File.Dispose();
            try { File.Delete(f.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string BatchFolder()
        {
            if (batch != null && Directory.Exists(batch) && Directory.GetCreationTimeUtc(batch) > DateTime.UtcNow.AddMinutes(-1)) return batch;
            Directory.CreateDirectory(folder);
            foreach (var old in new DirectoryInfo(folder).EnumerateDirectories().Where(d => d.CreationTimeUtc < DateTime.UtcNow.AddDays(-7)))
            {
                try { old.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            batch = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
            Directory.CreateDirectory(batch);
            return batch;
        }
    }
}
