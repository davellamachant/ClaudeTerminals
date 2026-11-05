using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LlamaTree.Platform;

/// <summary>
/// The JSON files the app keeps its lists in (sessions.json, projects.json and the rest), read and written so that a
/// damaged file never costs what was in it. A file that can't be parsed is moved aside as name.corrupt-yyyyMMdd-HHmmss-fff
/// (the last few are kept) instead of being saved over, and, where the store allows, the copy before it (name.bak, kept
/// by <see cref="Write"/>) is used in its place. What was set aside is told to the person once the window is ready
/// (<see cref="TakeProblems"/>).
/// </summary>
internal static class JsonFile
{
    /// <summary>How many set-aside copies of one file are kept (the newest).</summary>
    internal const int KeepCorrupt = 3;

    private static readonly ConcurrentQueue<string> problems = new();

    public static string BackupPath(string path) => path + ".bak";

    /// <summary>
    /// Reads <paramref name="path"/> as <typeparamref name="T"/>; null when there's no file (or it holds null). One that
    /// can't be parsed is set aside, then the backup is used (and put back in its place) if
    /// <paramref name="useBackup"/> and it's readable, else null. One that can't be read at all (locked, no access) is
    /// left alone and the backup used without putting it back.
    /// </summary>
    /// <param name="useBackup">
    /// False for a store where the older copy could undo something the person took back (e.g. a permission they
    /// withdrew): a damaged file there means starting again from nothing, which asks again.
    /// </param>
    public static T? Read<T>(string path, JsonSerializerOptions? options = null, bool useBackup = true) where T : class
    {
        string text;
        try
        {
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var kept = useBackup ? TryRead<T>(BackupPath(path), options) : null;
            Note($"{path} couldn't be read ({ex.Message})" + (kept != null ? "; the copy saved before it was used instead." : "."));
            return kept;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, options);
        }
        catch (Exception)   // JsonException mostly; anything the text makes deserializing throw means the same
        {
            var moved = Quarantine(path);
            var backup = BackupPath(path);
            var restored = useBackup ? TryRead<T>(backup, options) : null;
            if (restored != null && moved != null)
            {
                // Back in its place, so the next start finds it even if nothing is saved before then.
                try { File.Copy(backup, path, overwrite: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            Note($"{Path.GetFileName(path)} was damaged" +
                 (moved != null ? $"; it's kept as {moved}" : "") +
                 (restored != null ? ", and the copy saved before it was used instead." : ", so it starts empty."));
            return restored;
        }
    }

    /// <summary>
    /// Writes <paramref name="json"/> to <paramref name="path"/> in one step: to a temporary file first (flushed to the
    /// disk), which then replaces it. With <paramref name="keepBackup"/> what was there is kept as name.bak.
    /// </summary>
    public static void Write(string path, string json, bool keepBackup = true)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(Encoding.UTF8.GetBytes(json));
            file.Flush(flushToDisk: true);
        }
        if (keepBackup && File.Exists(path))
        {
            try
            {
                File.Replace(temp, path, BackupPath(path), ignoreMetadataErrors: true);
                return;
            }
            catch (IOException)
            {
                // Some file systems can't replace in one step: keep the backup by copying, then move as before. (A
                // failed replace may already have moved the file to the backup.)
                if (File.Exists(path)) File.Copy(path, BackupPath(path), overwrite: true);
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>What's been set aside or recovered since last asked, to tell the person (each told once).</summary>
    public static List<string> TakeProblems()
    {
        var taken = new List<string>();
        while (problems.TryDequeue(out var p)) taken.Add(p);
        return taken;
    }

    /// <summary>Moves a damaged file aside (keeping the newest <see cref="KeepCorrupt"/>); its new path, or null if it couldn't be.</summary>
    internal static string? Quarantine(string path)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var target = $"{path}.corrupt-{stamp}";
            for (var n = 2; File.Exists(target); n++) target = $"{path}.corrupt-{stamp}-{n}";
            File.Move(path, target);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            // The stamp sorts by when each was set aside (a move keeps the file's own times); the newest are kept.
            foreach (var old in Directory.GetFiles(folder, Path.GetFileName(path) + ".corrupt-*")
                         .OrderByDescending(f => f, StringComparer.Ordinal).Skip(KeepCorrupt))
            {
                try { File.Delete(old); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static T? TryRead<T>(string path, JsonSerializerOptions? options) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), options) : null;
        }
        catch (Exception)   // unreadable or damaged too: no backup to use
        {
            return null;
        }
    }

    private static void Note(string problem)
    {
        if (problems.Count < 20) problems.Enqueue(problem);
    }
}
