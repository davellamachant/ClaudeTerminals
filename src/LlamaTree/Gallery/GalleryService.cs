using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LlamaTree.Plugins;
using LlamaTree.Themes;
using LlamaTree.Updates;

namespace LlamaTree.Gallery;

/// <summary>A plugin <see cref="GalleryService"/> offers, as its catalog lists it.</summary>
/// <param name="Zip">Its release's zip: the plugin's folder at the zip's root.</param>
/// <param name="Sha256">What the zip must hash to.</param>
/// <param name="Icon">Its icon as a data: URL of an image, if the catalog carries one.</param>
internal sealed record GalleryPlugin(string Id, string Title, string Description, string Version, string? MinAppVersion, string Placement,
    IReadOnlyList<string> Access, bool Inbox, string Zip, string Sha256, string? Icon, string? Author);

/// <summary>A theme <see cref="GalleryService"/> offers: the theme itself is in the catalog, so it can be shown before it's installed.</summary>
/// <param name="Theme">The theme as the app reads it (see <see cref="ThemeCatalog.Parse"/>).</param>
/// <param name="File">What's written to the themes folder as &lt;id&gt;.json.</param>
internal sealed record GalleryTheme(string Id, string Version, string? MinAppVersion, string? Description, string? Author, ThemeInfo Theme, string File);

/// <summary>
/// Get plugins and Get themes: the catalogs the LlamaTree-Plugins and LlamaTree-Themes repositories publish on their
/// rolling "catalog" releases, and installing from them into the profile's plugins and themes folders. A plugin is its
/// release's zip, checked against the SHA-256 in the catalog before anything is unpacked, then checked the way the plugins
/// folder is read before it takes the place of the one there. A theme is data, carried whole in its catalog. Nothing is
/// fetched until you open Get plugins or Get themes, and nothing is sent but the anonymous requests for the files.
/// </summary>
internal static partial class GalleryService
{
    public const string PluginsRepository = "https://github.com/LlamachantTechnology/LlamaTree-Plugins";
    public const string ThemesRepository = "https://github.com/LlamachantTechnology/LlamaTree-Themes";

    /// <summary>For testing against a local server: plain http is then allowed, but only to this machine.</summary>
    private static readonly string? PluginCatalogOverride = Environment.GetEnvironmentVariable("LLAMATREE_PLUGIN_CATALOG_URL") is { Length: > 0 } p ? p : null;
    private static readonly string? ThemeCatalogOverride = Environment.GetEnvironmentVariable("LLAMATREE_THEME_CATALOG_URL") is { Length: > 0 } t ? t : null;

    public static string PluginCatalogUrl => PluginCatalogOverride ?? PluginsRepository + "/releases/download/catalog/catalog.json";
    public static string ThemeCatalogUrl => ThemeCatalogOverride ?? ThemesRepository + "/releases/download/catalog/catalog.json";

    /// <summary>Where a plugin's zip may come from: its repository's releases.</summary>
    private const string ZipPrefix = PluginsRepository + "/releases/download/";

    public const int MaxCatalogBytes = 4 * 1024 * 1024;
    public const int MaxZipBytes = 20 * 1024 * 1024;
    public const long MaxUnpackedBytes = 50L * 1024 * 1024;
    public const int MaxZipEntries = 2000;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // GitHub answers a release download with a redirect to its file store; the hash is checked whatever the address.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LlamaTree", UpdateService.CurrentVersion.ToString()));
        return http;
    }

    private static readonly SemaphoreSlim Installing = new(1, 1);

    // ---- the catalogs

    /// <summary>What a catalog said, kept for a few minutes so switching between Plugins and Themes doesn't fetch again.</summary>
    private sealed record Cached<T>(DateTime At, List<T> Items, List<string> Problems);
    private static Cached<GalleryPlugin>? pluginCache;
    private static Cached<GalleryTheme>? themeCache;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    public static async Task<(List<GalleryPlugin> Plugins, List<string> Problems)> PluginsAsync(bool refresh, CancellationToken ct = default)
    {
        if (!refresh && pluginCache is { } c && DateTime.UtcNow - c.At < CacheFor) return (c.Items, c.Problems);
        var problems = new List<string>();
        var list = ParsePlugins(await FetchAsync(PluginCatalogUrl, PluginCatalogOverride != null, MaxCatalogBytes, ct), problems);
        pluginCache = new(DateTime.UtcNow, list, problems);
        return (list, problems);
    }

    public static async Task<(List<GalleryTheme> Themes, List<string> Problems)> ThemesAsync(bool refresh, CancellationToken ct = default)
    {
        if (!refresh && themeCache is { } c && DateTime.UtcNow - c.At < CacheFor) return (c.Items, c.Problems);
        var problems = new List<string>();
        var list = ParseThemes(await FetchAsync(ThemeCatalogUrl, ThemeCatalogOverride != null, MaxCatalogBytes, ct), problems);
        themeCache = new(DateTime.UtcNow, list, problems);
        return (list, problems);
    }

    /// <summary>
    /// Reads a plugin catalog: <c>{ "schema": 1, "plugins": [{ "id", "title", "description", "version", "minAppVersion",
    /// "placement", "access", "inbox", "zip", "sha256", "icon"?, "author"? }] }</c>. An entry that isn't usable is left out,
    /// with why; so is a second entry with the same id.
    /// </summary>
    internal static List<GalleryPlugin> ParsePlugins(string json, List<string> problems)
    {
        var result = new List<GalleryPlugin>();
        using var doc = ParseCatalog(json, "plugins", out var items);
        foreach (var e in items)
        {
            var id = Str(e, "id");
            if (!PluginCatalog.IsValidId(id) || result.Any(r => r.Id == id))
            {
                problems.Add($"{id ?? "A plugin"}: its id is missing, isn't letters, digits and hyphens, or is listed twice.");
                continue;
            }
            var version = Str(e, "version");
            var zip = Str(e, "zip");
            var sha = Str(e, "sha256")?.ToLowerInvariant();
            if (version == null || !PluginCatalog.VersionPattern().IsMatch(version) || zip == null || sha == null || !Sha256Pattern().IsMatch(sha))
            {
                problems.Add($"{id}: it needs a version (major.minor.patch), a zip and its SHA-256.");
                continue;
            }
            var placement = Str(e, "placement") == "panel" ? "panel" : "tab";
            var access = e.TryGetProperty("access", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
            result.Add(new GalleryPlugin(id!, Short(Str(e, "title"), 60) ?? id!, Short(Str(e, "description"), 300) ?? "", version, MinVersion(e),
                placement, access, e.TryGetProperty("inbox", out var inbox) && inbox.ValueKind == JsonValueKind.True, zip, sha,
                Str(e, "icon") is { } icon && IconPattern().IsMatch(icon) && icon.Length <= 100_000 ? icon : null,
                Short(Str(e, "author"), 60)));
        }
        result.Sort((x, y) => StringComparer.CurrentCultureIgnoreCase.Compare(x.Title, y.Title));
        return result;
    }

    /// <summary>
    /// Reads a theme catalog: <c>{ "schema": 1, "themes": [{ "id", "version", "minAppVersion"?, "description"?, "author"?,
    /// "theme": { …the theme's file… } }] }</c>. A theme the app can't read is left out, with why.
    /// </summary>
    internal static List<GalleryTheme> ParseThemes(string json, List<string> problems)
    {
        var result = new List<GalleryTheme>();
        using var doc = ParseCatalog(json, "themes", out var items);
        foreach (var e in items)
        {
            var id = Str(e, "id");
            if (!PluginCatalog.IsValidId(id) || id is "system" or "light" or "dark" || result.Any(r => r.Id == id))
            {
                problems.Add($"{id ?? "A theme"}: its id is missing, isn't letters, digits and hyphens, is light, dark or system, or is listed twice.");
                continue;
            }
            var version = Str(e, "version");
            if (version == null || !PluginCatalog.VersionPattern().IsMatch(version) || !e.TryGetProperty("theme", out var t) || t.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{id}: it needs a version (major.minor.patch) and the theme.");
                continue;
            }
            // The file carries its version, which is how the themes folder knows what it has.
            var file = JsonNode.Parse(t.GetRawText())!.AsObject();
            file["version"] = version;
            var text = file.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (text.Length > ThemeCatalog.MaxFileBytes)
            {
                problems.Add($"{id}: it's larger than a theme can be.");
                continue;
            }
            using var parsed = JsonDocument.Parse(text);
            // What this copy of the app doesn't know (a colour added since, say) is left out of the theme, as it would be from the file.
            if (ThemeCatalog.Parse(id!, parsed.RootElement, false, id + ".json", []) is not { } theme)
            {
                problems.Add($"{id}: this copy of LlamaTree can't read it.");
                continue;
            }
            result.Add(new GalleryTheme(id!, version, MinVersion(e), Short(Str(e, "description"), 300), Short(Str(e, "author"), 60), theme, text));
        }
        result.Sort((x, y) => StringComparer.CurrentCultureIgnoreCase.Compare(x.Theme.Name, y.Theme.Name));
        return result;
    }

    private static JsonDocument ParseCatalog(string json, string list, out List<JsonElement> items)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var s) || !s.TryGetInt32(out var schema))
        {
            doc.Dispose();
            throw new InvalidOperationException("The catalog isn't one LlamaTree can read.");
        }
        if (schema > 1)
        {
            doc.Dispose();
            throw new InvalidOperationException("The catalog is newer than this copy of LlamaTree: update LlamaTree to see it.");
        }
        items = root.TryGetProperty(list, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList() : [];
        return doc;
    }

    // ---- where each stands here

    /// <summary>
    /// What can be done with a plugin or theme from a catalog, given the one here with that id (null: none) and its version:
    /// "install", "installed", "update", "needs-app" (it needs a newer LlamaTree), "yours" (a plugin or theme of your own
    /// already has its id) or "built-in" (a theme the app comes with has its id).
    /// </summary>
    internal static string StateOf(string version, string? minAppVersion, bool haveOne, string? haveVersion, bool builtIn = false)
    {
        if (builtIn) return "built-in";
        if (haveOne && haveVersion == null) return "yours";
        var newer = !haveOne || Version.Parse(version) > Version.Parse(haveVersion!);
        if (newer && minAppVersion != null && Version.Parse(minAppVersion) > UpdateService.CurrentVersion) return "needs-app";
        return !haveOne ? "install" : newer ? "update" : "installed";
    }

    // ---- plugins

    /// <summary>Downloads the plugin's zip, checks it and puts it in <paramref name="pluginFolder"/>, in place of an older copy.</summary>
    public static async Task InstallPluginAsync(GalleryPlugin plugin, string pluginFolder, string staging, CancellationToken ct = default)
    {
        if (!AllowedZip(plugin.Zip))
            throw new InvalidOperationException($"{plugin.Title}'s download isn't one of the LlamaTree-Plugins releases, so it won't be used.");
        var zip = await DownloadAsync(plugin.Zip, PluginCatalogOverride != null, MaxZipBytes, ct);
        await Installing.WaitAsync(ct);
        try { InstallPluginZip(zip, plugin.Id, plugin.Sha256, pluginFolder, staging); }
        finally { Installing.Release(); }
    }

    /// <summary>
    /// Checks <paramref name="zip"/> against <paramref name="sha256"/>, unpacks it in <paramref name="staging"/> (each file
    /// under &lt;id&gt;/, within the size limits), checks it's a plugin the plugins folder would load, then moves it into
    /// <paramref name="pluginFolder"/>, the older copy out of the way first and back if the move fails.
    /// </summary>
    internal static void InstallPluginZip(byte[] zip, string id, string sha256, string pluginFolder, string staging)
    {
        if (!PluginCatalog.IsValidId(id)) throw new InvalidOperationException("That isn't a plugin's id.");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(zip)), sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The download doesn't match the catalog's checksum, so it wasn't installed. Try again later.");

        var work = Path.Combine(staging, Guid.NewGuid().ToString("N")[..12]);
        var unpacked = Path.Combine(work, "new");
        try
        {
            Unpack(zip, id, unpacked);
            var (found, problems) = PluginCatalog.Load(unpacked);
            if (found.SingleOrDefault(p => p.Id == id) == null)
                throw new InvalidOperationException("The download isn't a plugin LlamaTree can load" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : "."));

            Directory.CreateDirectory(pluginFolder);
            var target = Path.Combine(pluginFolder, id);
            var old = Path.Combine(work, "old");
            var had = Directory.Exists(target);
            if (had)
            {
                CheckReplaceable(target);
                Directory.Move(target, old);
            }
            try { Directory.Move(Path.Combine(unpacked, id), target); }
            catch when (had)
            {
                Directory.Move(old, target);
                throw;
            }
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("The download isn't a zip LlamaTree can read.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"The plugin couldn't be put in the plugins folder ({ex.Message}). Close its tab and try again.", ex);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Unpacks the zip's &lt;id&gt;/ folder into <paramref name="into"/>; anything else in it, or a path out of it, is refused.</summary>
    private static void Unpack(byte[] zip, string id, string into)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxZipEntries) throw new InvalidOperationException($"The download holds more than {MaxZipEntries} files.");
        var root = Path.GetFullPath(into) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.Split('/');
            if (!name.StartsWith(id + "/", StringComparison.Ordinal) || Path.IsPathRooted(name) || parts.Any(p => p is ".." or "." || p.Contains(':')))
                throw new InvalidOperationException($"The download holds {entry.FullName}, outside a folder called {id}.");
            var path = Path.GetFullPath(Path.Combine(into, name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The download holds {entry.FullName}, outside its folder.");
            if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = File.Create(path);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                total += read;   // counted as it's written: an entry's stated size can't be trusted
                if (total > MaxUnpackedBytes) throw new InvalidOperationException($"The download unpacks to more than {MaxUnpackedBytes / 1024 / 1024} MB.");
                output.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>Takes the plugin out of the plugins folder. Only one from Get plugins (it has a version) is removed this way.</summary>
    public static void RemovePlugin(string pluginFolder, string id)
    {
        if (!PluginCatalog.IsValidId(id)) throw new InvalidOperationException("That isn't a plugin's id.");
        var target = Path.Combine(pluginFolder, id);
        if (!Directory.Exists(target)) return;
        CheckReplaceable(target);
        try { Directory.Delete(target, true); }
        catch (IOException ex) { throw new InvalidOperationException($"The plugin couldn't be removed ({ex.Message}). Close its tab and try again.", ex); }
    }

    /// <summary>A folder Get plugins may replace or remove: a real folder (not a link) holding a plugin whose plugin.json has a version.</summary>
    private static void CheckReplaceable(string folder)
    {
        if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("The plugin's folder is a link to another folder, so it's left alone.");
        var (found, _) = PluginCatalog.Load(Path.GetDirectoryName(folder)!);
        var p = found.FirstOrDefault(x => string.Equals(x.Folder, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
        if (p == null || p.Version == null)
            throw new InvalidOperationException($"{folder} holds a plugin of your own, or something else, so it's left alone.");
    }

    // ---- themes

    /// <summary>Writes the theme to <paramref name="themeFolder"/> as &lt;id&gt;.json, in place of an older copy from Get themes.</summary>
    public static void InstallTheme(GalleryTheme theme, string themeFolder, string builtIn)
    {
        if (File.Exists(Path.Combine(builtIn, theme.Id + ".json")))
            throw new InvalidOperationException($"LlamaTree already comes with a theme called {theme.Id}.");
        var path = Path.Combine(themeFolder, theme.Id + ".json");
        if (File.Exists(path) && InstalledThemeVersion(path) == null)
            throw new InvalidOperationException($"A theme of your own is called {theme.Id}.json, so it's left alone.");
        Directory.CreateDirectory(themeFolder);
        var temp = path + ".download";
        File.WriteAllText(temp, theme.File);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Takes a theme from Get themes out of the themes folder; one of your own (no version) is left alone.</summary>
    public static void RemoveTheme(string themeFolder, string id)
    {
        if (!PluginCatalog.IsValidId(id)) throw new InvalidOperationException("That isn't a theme's id.");
        var path = Path.Combine(themeFolder, id + ".json");
        if (!File.Exists(path)) return;
        if (InstalledThemeVersion(path) == null) throw new InvalidOperationException($"{id}.json is a theme of your own, so it's left alone.");
        File.Delete(path);
    }

    private static string? InstalledThemeVersion(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && Str(doc.RootElement, "version") is { } v && PluginCatalog.VersionPattern().IsMatch(v) ? v : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    // ---- downloads

    private static async Task<string> FetchAsync(string url, bool testing, int max, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await DownloadAsync(url, testing, max, ct));

    private static async Task<byte[]> DownloadAsync(string url, bool testing, int max, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !(u.Scheme == Uri.UriSchemeHttps || (testing && u.IsLoopback)))
            throw new InvalidOperationException("That address isn't one LlamaTree downloads from.");
        HttpResponseMessage response;
        try { response = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException("GitHub couldn't be reached. Check your connection and try again.", ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "Nothing has been published there yet." : $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}. Try again later.");
            if (response.Content.Headers.ContentLength > max) throw new InvalidOperationException("The download is larger than LlamaTree accepts.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > max) throw new InvalidOperationException("The download is larger than LlamaTree accepts.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }

    internal static bool AllowedZip(string url) =>
        url.StartsWith(ZipPrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out _)
        || PluginCatalogOverride != null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsLoopback;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    // An image, inline: shown in an <img>, so it runs no script.
    [GeneratedRegex(@"^data:image/(?:svg\+xml|png|gif|jpeg|webp|x-icon);base64,[A-Za-z0-9+/]+=*$")]
    private static partial Regex IconPattern();

    private static string? MinVersion(JsonElement e) => Str(e, "minAppVersion") is { } v && PluginCatalog.VersionPattern().IsMatch(v) ? v : null;

    private static string? Short(string? s, int max) => s?.Trim() is { Length: > 0 } t ? t.Length > max ? t[..max] : t : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
