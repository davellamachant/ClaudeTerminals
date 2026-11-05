using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlamaTree.Sessions;

namespace LlamaTree.Git;

/// <summary>A setting of the repository's own that can make git run a program.</summary>
/// <param name="Where">Which of the repository's files it's in: "local", "worktree", "worktree name", "submodule name", or "hook".</param>
internal sealed record RiskyKey(string Key, string? Value, string Where);

/// <summary>What <see cref="RepoTrust"/> found in one repository (all its worktrees and submodules share it).</summary>
/// <param name="CommonDir">The repository's common git dir; trust is kept under it.</param>
/// <param name="Hash">Of <paramref name="Keys"/> and their values: trust holds only while this is unchanged.</param>
/// <param name="Overrides">Config given to every git call while the repository isn't trusted (see <see cref="RepoTrust.OverridesFor"/>).</param>
internal sealed record RepoSettings(string CommonDir, IReadOnlyList<RiskyKey> Keys, string Hash, IReadOnlyList<(string Key, string Value)> Overrides)
{
    public bool Risky => Keys.Count > 0;
}

/// <summary>What the page is told: the risky settings and whether the person trusts them. Null when there are none.</summary>
internal sealed record RepoTrustInfo(string Repo, string Hash, bool Trusted, IReadOnlyList<RiskyKey> Keys);

/// <summary>Git refused to run a command the person started because the repository's own settings aren't trusted yet.</summary>
internal sealed class RepoTrustRequiredException(string message) : InvalidOperationException(message);

/// <summary>
/// The repositories the person trusts, kept in repo-trust.json under the app data folder: each by its common git dir,
/// with the hash of the risky settings it had when trusted. Only trust is kept; "Not now" is the page's to remember.
/// </summary>
internal sealed class RepoTrustStore
{
    internal sealed record Entry(string Hash, DateTimeOffset Trusted);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string? path;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries;

    /// <param name="path">Where it's kept; null keeps it in memory only (tests).</param>
    public RepoTrustStore(string? path)
    {
        this.path = path;
        entries = path == null ? [] : Load(path);
    }

    /// <summary>Trusted with exactly these settings; a repository whose settings changed since is asked about again.</summary>
    public bool IsTrusted(string repo, string hash)
    {
        lock (gate) return entries.TryGetValue(Key(repo), out var e) && e.Hash == hash;
    }

    public void Trust(string repo, string hash)
    {
        lock (gate)
        {
            entries[Key(repo)] = new Entry(hash, DateTimeOffset.UtcNow);
            Save();
        }
    }

    public void Revoke(string repo)
    {
        lock (gate)
        {
            if (entries.Remove(Key(repo))) Save();
        }
    }

    /// <summary>The same folder however it's spelt: full path, no trailing separator, and case-folded where the file system is.</summary>
    internal static string Key(string repo)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo)).Replace('\\', '/');
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToLowerInvariant() : full;
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), Options) is { } read)
                return read.Where(x => !string.IsNullOrEmpty(x.Key) && x.Value is { Hash.Length: > 0 }).ToDictionary(x => x.Key, x => x.Value);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file loses the trust: each repository asks again, which is the safe way round.
        }
        return [];
    }

    private void Save()
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this run; written next time something changes.
        }
    }
}

/// <summary>
/// Per-repository trust in a repository's own settings. A repository's .git/config (or a file it includes, a worktree's
/// config.worktree, a submodule's config, its hooks folder) can name programs for git to run. The person's global and
/// system config are theirs and always apply; the repository's own are found here by reading its config files, which
/// runs nothing. Until the person trusts the repository:
/// <list type="bullet">
/// <item>every git call the app makes in it gets <see cref="RepoSettings.Overrides"/>: each filter driver the repository
/// defines is turned off (an LFS file may then show as changed), the one kind of setting an automatic call (status, on
/// start, on change, every 15 s) would still run;</item>
/// <item>commands the person starts that would run the others (fetch, pull, push, commit, stage, undo, switching
/// branches, adding a worktree) are refused with <see cref="RepoTrustRequiredException"/>, and the page asks.</item>
/// </list>
/// Repositories with none of these settings behave exactly as before.
/// </summary>
internal static class RepoTrust
{
    private static RepoTrustStore? store;

    /// <summary>Where trust is kept (repo-trust.json in the app data folder); tests put an in-memory one here.</summary>
    internal static RepoTrustStore Store
    {
        // Made once even when several git calls ask at the same time: a second copy would lose trust given to the first.
        get => LazyInitializer.EnsureInitialized(ref store, () => new RepoTrustStore(Path.Combine(SessionStore.AppDataFolder, "repo-trust.json")));
        set => store = value;
    }

    /// <param name="Marker">Whether <c>dir/.git</c> existed when this was read: a folder that becomes its own repository is looked up again.</param>
    private sealed record Location(string GitDir, string CommonDir, bool Marker);

    private sealed record Cached(string Stamp, RepoSettings Settings);

    private static readonly ConcurrentDictionary<string, Location> Locations = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Cached> Settings = new(StringComparer.Ordinal);

    /// <summary>Forgets every repository read so far (tests).</summary>
    internal static void ClearCache()
    {
        Locations.Clear();
        Settings.Clear();
    }

    /// <summary>
    /// The risky settings of the repository containing <paramref name="dir"/>, or null when it isn't in one. Read again only
    /// when one of the files it came from (or the folders that hold them) changed, so the 15 s loop costs a few file stats.
    /// </summary>
    public static async Task<RepoSettings?> GetSettingsAsync(string dir, CancellationToken ct = default)
    {
        if (await LocateAsync(dir, ct) is not { } loc) return null;
        var key = RepoTrustStore.Key(loc.CommonDir);
        if (Settings.TryGetValue(key, out var hit) && hit.Stamp == Stamp(loc.CommonDir)) return hit.Settings;
        var settings = await ReadAsync(dir, loc.CommonDir, ct);
        // Stamped after reading, from the files this read found, so an included file it added is watched from now on.
        Settings[key] = new Cached(Stamp(loc.CommonDir), settings);
        return settings;
    }

    /// <summary>For the page: null when the repository has no risky settings (or <paramref name="dir"/> isn't in one).</summary>
    public static async Task<RepoTrustInfo?> DescribeAsync(string dir, CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(dir, ct);
        return s is { Risky: true } ? new RepoTrustInfo(s.CommonDir, s.Hash, Store.IsTrusted(s.CommonDir, s.Hash), s.Keys) : null;
    }

    /// <summary>The config every git call in <paramref name="dir"/> gets: none once trusted, or when there's nothing to turn off.</summary>
    public static async Task<IReadOnlyList<(string Key, string Value)>> OverridesForAsync(string dir, CancellationToken ct = default)
    {
        try
        {
            var s = await GetSettingsAsync(dir, ct);
            return s is { Risky: true } && !Store.IsTrusted(s.CommonDir, s.Hash) ? s.Overrides : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return [];   // git itself is missing, or the folder is unreadable: the call will fail on its own
        }
    }

    /// <summary>
    /// Why the person's <paramref name="action"/> ("fetch", say) can't run in <paramref name="dir"/> yet, or null if it can:
    /// the repository has risky settings they haven't trusted.
    /// </summary>
    public static async Task<string?> RefusalAsync(string dir, string action)
    {
        var s = await GetSettingsAsync(dir);
        if (s is not { Risky: true } || Store.IsTrusted(s.CommonDir, s.Hash)) return null;
        return $"This repository's own settings can run programs ({ShortList(s.Keys)}), so LlamaTree didn't {action}. Trust the repository to {action}.";
    }

    /// <summary>Trusts (or stops trusting) the repository containing <paramref name="dir"/>, given the hash the person was shown.</summary>
    /// <returns>False if its settings have changed since <paramref name="hash"/> (the person has to look again).</returns>
    public static async Task<bool> SetAsync(string dir, bool trusted, string? hash)
    {
        var s = await GetSettingsAsync(dir) ?? throw new InvalidOperationException("This folder isn't a Git repository.");
        if (!trusted)
        {
            Store.Revoke(s.CommonDir);
            return true;
        }
        if (!s.Risky) return true;
        if (hash != s.Hash) return false;
        Store.Trust(s.CommonDir, s.Hash);
        return true;
    }

    /// <summary>"core.sshcommand, credential.helper and 2 more": a few names for a message.</summary>
    internal static string ShortList(IReadOnlyList<RiskyKey> keys, int max = 4)
    {
        var names = keys.Select(k => k.Where == "hook" ? "hook " + k.Key : k.Key).Distinct().ToList();
        return names.Count <= max ? string.Join(", ", names) : string.Join(", ", names.Take(max)) + $" and {names.Count - max} more";
    }

    // ---- Finding the repository

    private static async Task<Location?> LocateAsync(string dir, CancellationToken ct)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!Directory.Exists(full)) return null;
        var marker = Path.Exists(Path.Combine(full, ".git"));
        if (Locations.TryGetValue(full, out var known) && known.Marker == marker && Directory.Exists(known.CommonDir)) return known;
        var r = await GitService.RunUnguardedAsync(full, ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir"], ct: ct,
            timeout: TimeSpan.FromSeconds(20));
        var lines = r.Ok ? r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        if (lines.Length < 2) return null;   // not in a repository (not cached: it may become one)
        var loc = new Location(Path.GetFullPath(lines[0]), Path.GetFullPath(lines[1]), marker);
        Locations[full] = loc;
        return loc;
    }

    /// <summary>The repository's own config files: its config, each worktree's config.worktree and each submodule's config.</summary>
    private static List<(string File, string Where)> ConfigFiles(string commonDir)
    {
        var list = new List<(string, string)> { (Path.Combine(commonDir, "config"), "local"), (Path.Combine(commonDir, "config.worktree"), "worktree") };
        foreach (var wt in SafeDirectories(Path.Combine(commonDir, "worktrees")))
            list.Add((Path.Combine(wt, "config.worktree"), "worktree " + Path.GetFileName(wt)));
        void Modules(string folder, string prefix, int depth)
        {
            if (depth > 4) return;
            foreach (var m in SafeDirectories(folder))
            {
                var name = prefix + Path.GetFileName(m);
                if (File.Exists(Path.Combine(m, "config"))) list.Add((Path.Combine(m, "config"), "submodule " + name));
                Modules(Path.Combine(m, "modules"), name + "/", depth + 1);
                // A submodule in a subfolder (libs/foo) is kept as modules/libs/foo: look below folders without a config too.
                if (!File.Exists(Path.Combine(m, "config"))) Modules(m, name + "/", depth + 1);
            }
        }
        Modules(Path.Combine(commonDir, "modules"), "", 0);
        return list;
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder).Where(d => !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)).Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Hooks git would run for the person's commands: anything in the hooks folder but git's own samples.</summary>
    private static List<string> HookFiles(string commonDir)
    {
        try
        {
            var hooks = Path.Combine(commonDir, "hooks");
            return Directory.Exists(hooks)
                ? Directory.EnumerateFiles(hooks).Where(f => !f.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Size and time of every file and folder the settings come from (or would, once created). Equal stamps: nothing to
    /// read again.
    /// </summary>
    private static string Stamp(string commonDir)
    {
        var files = ConfigFiles(commonDir).Select(f => f.File).ToList();
        files.AddRange(HookFiles(commonDir));
        files.Add(Path.Combine(commonDir, "hooks"));
        files.Add(Path.Combine(commonDir, "worktrees"));
        files.Add(Path.Combine(commonDir, "modules"));
        if (IncludedFiles.TryGetValue(RepoTrustStore.Key(commonDir), out var included)) files.AddRange(included);
        var sb = new StringBuilder();
        foreach (var f in files.Distinct(StringComparer.Ordinal))
        {
            sb.Append(f).Append('|');
            try
            {
                var info = new FileInfo(f);
                if (info.Exists) sb.Append(info.LastWriteTimeUtc.Ticks).Append('|').Append(info.Length);
                else if (Directory.Exists(f)) sb.Append('d').Append(Directory.GetLastWriteTimeUtc(f).Ticks);
                else sb.Append('-');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sb.Append('?'); }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The files a repository's config includes, per repository, so their changes are noticed too.</summary>
    private static readonly ConcurrentDictionary<string, List<string>> IncludedFiles = new(StringComparer.Ordinal);

    // ---- Reading the settings

    private static async Task<RepoSettings> ReadAsync(string dir, string commonDir, CancellationToken ct)
    {
        var keys = new List<RiskyKey>();
        var included = new List<string>();
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string File, string Where)>(ConfigFiles(commonDir).Where(f => File.Exists(f.File)));
        while (queue.Count > 0)
        {
            var (file, where) = queue.Dequeue();
            if (!read.Add(Path.GetFullPath(file))) continue;
            // --file reads that one file (and what it includes), never the person's global or system config. Reading runs nothing.
            var r = await GitService.RunUnguardedAsync(dir, ["config", "--file", file, "--includes", "--list", "-z", "--show-origin"], ct: ct,
                timeout: TimeSpan.FromSeconds(20));
            if (!r.Ok) continue;
            foreach (var (origin, key, value) in ParseList(r.Text))
            {
                if (origin != null && !string.Equals(Path.GetFullPath(origin), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                    included.Add(Path.GetFullPath(origin));
                if (IsInclude(key) && value is { Length: > 0 } target)
                {
                    var path = ResolveInclude(target, origin ?? file);
                    if (path != null && IsInside(path, commonDir))
                    {
                        // Inside the repository: read it whatever its condition, so a key can't wait behind includeIf.
                        if (File.Exists(path)) { queue.Enqueue((path, where)); included.Add(path); }
                        continue;
                    }
                    keys.Add(new RiskyKey(key, value, where));   // settings from outside the repository: reported
                    continue;
                }
                if (IsRisky(key, value)) keys.Add(new RiskyKey(key, value, where));
            }
        }
        foreach (var hook in HookFiles(commonDir))
        {
            string digest;
            try { digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(hook, ct)))[..16]; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { digest = "?"; }
            keys.Add(new RiskyKey(Path.GetFileName(hook), digest, "hook"));
        }
        keys = keys.DistinctBy(k => (k.Key, k.Value, k.Where)).OrderBy(k => k.Where == "hook" ? 1 : 0).ThenBy(k => k.Key, StringComparer.Ordinal).ToList();
        IncludedFiles[RepoTrustStore.Key(commonDir)] = included.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new RepoSettings(commonDir, keys, Hash(keys), Overrides(keys));
    }

    /// <summary>Parses <c>git config --list -z --show-origin</c>: origin NUL, then key, a newline and the value (none for a bare key) NUL.</summary>
    internal static IEnumerable<(string? Origin, string Key, string? Value)> ParseList(string text)
    {
        var parts = text.Split('\0');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var origin = parts[i].StartsWith("file:", StringComparison.Ordinal) ? parts[i][5..] : null;
            var entry = parts[i + 1];
            var nl = entry.IndexOf('\n');
            yield return nl < 0 ? (origin, entry, null) : (origin, entry[..nl], entry[(nl + 1)..]);
        }
    }

    private static bool IsInclude(string key)
    {
        var (section, sub, name) = Split(key);
        return name == "path" && (section == "include" && sub == null || section == "includeif" && sub != null);
    }

    /// <summary>An include's file as git finds it: ~ is the home folder, and a relative path is relative to the including file.</summary>
    private static string? ResolveInclude(string target, string includingFile)
    {
        try
        {
            if (target.StartsWith("~/", StringComparison.Ordinal))
                return Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target[2..]));
            return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(includingFile))!, target));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool IsInside(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFullPath(path).StartsWith(root, comparison);
    }

    /// <summary>Section and name lower-cased, as git lists them; the subsection (a driver or remote name) as written.</summary>
    internal static (string Section, string? Sub, string Name) Split(string key)
    {
        var first = key.IndexOf('.');
        var last = key.LastIndexOf('.');
        if (first < 0) return (key.ToLowerInvariant(), null, "");
        var section = key[..first].ToLowerInvariant();
        var name = key[(last + 1)..].ToLowerInvariant();
        return (section, last > first ? key[(first + 1)..last] : null, name);
    }

    private static readonly HashSet<string> CoreKeys = ["sshcommand", "askpass", "editor", "pager", "hookspath", "gitproxy", "alternaterefscommand"];

    /// <summary>
    /// Whether this setting, in a repository's own config, can make git run a program. Settings that switch the thing off
    /// (an empty helper list, core.fsmonitor = false, a protocol set to never) aren't risky.
    /// </summary>
    internal static bool IsRisky(string key, string? value)
    {
        var (section, sub, name) = Split(key);
        var v = value?.Trim() ?? "";
        static bool IsFalse(string v) => v.Length == 0 || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v == "0";
        return section switch
        {
            "filter" => sub != null && name is "clean" or "smudge" or "process" && v.Length > 0,
            "diff" => sub != null ? name is "textconv" or "command" && v.Length > 0 : name == "external" && v.Length > 0,
            "merge" => sub != null && name == "driver" && v.Length > 0,
            "core" => sub == null && (CoreKeys.Contains(name) && v.Length > 0 || name == "fsmonitor" && !IsFalse(v)),
            "credential" => name == "helper" && v.Length > 0,
            "gpg" => name == "program" && v.Length > 0 || sub != null && sub.Equals("ssh", StringComparison.OrdinalIgnoreCase) && name == "defaultkeycommand" && v.Length > 0,
            "remote" => sub != null && name is "uploadpack" or "receivepack" or "vcs" && v.Length > 0,
            "url" => sub != null && name is "insteadof" or "pushinsteadof" && RunsLocally(sub),
            "protocol" => name == "allow" && !v.Equals("never", StringComparison.OrdinalIgnoreCase),
            "sequence" => sub == null && name == "editor" && v.Length > 0,
            "uploadpack" => sub == null && name == "packobjectshook" && v.Length > 0,
            "submodule" => sub != null && name == "update" && v.StartsWith('!'),
            "hook" => sub != null && name == "command" && v.Length > 0,
            _ => false,
        };
    }

    /// <summary>A URL that runs a command (ext::, fd::) or reaches another repository on this computer, whose own config then applies.</summary>
    private static bool RunsLocally(string url) =>
        url.StartsWith("ext::", StringComparison.OrdinalIgnoreCase) || url.StartsWith("fd::", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || url.StartsWith('/') || url.StartsWith('\\') || url.StartsWith('.')
        || url.StartsWith('~') || url.Length >= 2 && char.IsAsciiLetter(url[0]) && url[1] == ':' && (url.Length == 2 || url[2] is '/' or '\\');

    private static string Hash(IReadOnlyList<RiskyKey> keys)
    {
        var sb = new StringBuilder();
        foreach (var k in keys) sb.Append(k.Where).Append('\0').Append(k.Key).Append('\0').Append(k.Value ?? "\u0001").Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// What turns the risky settings off for the app's own git calls. Each filter driver the repository defines is
    /// emptied (git then reads and writes the file as it is) and made not required, which an automatic status would
    /// otherwise run. The rest are never reached by an automatic call (no network, no hooks, no editor, no merge; diffs
    /// pass --no-ext-diff and --no-textconv; fsmonitor and the pager are off for every call), and the person's own
    /// commands that would reach them are refused until the repository is trusted.
    /// </summary>
    internal static IReadOnlyList<(string Key, string Value)> Overrides(IReadOnlyList<RiskyKey> keys)
    {
        var list = new List<(string, string)>();
        foreach (var driver in keys.Select(k => Split(k.Key)).Where(k => k.Section == "filter" && k.Sub != null).Select(k => k.Sub!).Distinct(StringComparer.Ordinal))
        {
            list.Add(($"filter.{driver}.clean", ""));
            list.Add(($"filter.{driver}.smudge", ""));
            list.Add(($"filter.{driver}.process", ""));
            list.Add(($"filter.{driver}.required", "false"));
        }
        return list;
    }
}
