using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeTerminals.Plugins;

/// <summary>
/// A plugin: a web page of its own, shown in a tab. <see cref="Id"/> is its folder's name, which is also its address
/// (https://&lt;id&gt;.plugin.local/), so every plugin has an origin of its own, apart from the app's and each other's.
/// </summary>
/// <param name="Placement">Where its page shows: "tab" (a document tab, the default) or "panel" (a tab in the side panel).</param>
/// <param name="Menu">Items it adds to menus (see <see cref="PluginMenuItem"/>); each opens the plugin and sends it the item's command.</param>
/// <param name="Toolbar">Its button in the title bar, beside New Session and Git Changes, if it has one.</param>
internal sealed record PluginInfo(string Id, string Title, string? Description, string Entry, string Folder, string Placement,
    IReadOnlyList<PluginMenuItem> Menu, PluginButton? Toolbar = null)
{
    public string Host => PluginCatalog.HostFor(Id);
    public string Url => $"https://{Host}/{Entry}";
}

/// <param name="Where">The menus it's on: "palette" (the palette button's Plugins section), "project" (a project's
/// right-click menu), "session" (a session's ⋯ menu, on its tile) or "sessionTab" (a session tab's right-click menu).</param>
internal sealed record PluginMenuItem(string Label, string Command, IReadOnlyList<string> Where);

/// <summary>
/// A plugin's title bar button: an icon from its folder, never text, so it can't pass for one of the app's own buttons;
/// its tooltip starts with the plugin's name. Clicking it opens the plugin, sending the command if there is one.
/// <see cref="Icon"/> is the image itself, as a data: URL: the app page can't load files from a plugin's address (other
/// origins are refused, so plugins can't read each other's files either), and an image shown that way runs no script.
/// </summary>
internal sealed record PluginButton(string Icon, string? Tooltip, string? Command);

/// <summary>
/// Finds the plugins in the profile's plugins folder: each subfolder holding a plugin.json such as
/// <c>{ "title": "Floor plan", "description": "…", "entry": "index.html", "placement": "panel",
/// "menu": [{ "label": "…", "command": "…", "where": ["session", "sessionTab"] }] }</c>. A plugin is only a page: it runs in a
/// sandboxed frame, can't reach the app's bridge, and is given no session, file or terminal.
/// </summary>
internal static partial class PluginCatalog
{
    public const string Manifest = "plugin.json";
    public const string HostSuffix = ".plugin.local";

    public static string HostFor(string id) => id + HostSuffix;

    /// <summary>True for an address in a plugin's origin (any plugin).</summary>
    public static bool IsPluginUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        u.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase);

    // A folder name that's a valid host name label, so it can be the plugin's address.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex IdPattern();

    /// <summary>The plugins in <paramref name="folder"/>, by title; folders that aren't valid plugins are left out, with why.</summary>
    public static (List<PluginInfo> Plugins, List<string> Problems) Load(string folder)
    {
        var plugins = new List<PluginInfo>();
        var problems = new List<string>();
        if (!Directory.Exists(folder)) return (plugins, problems);
        foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            var manifest = Path.Combine(dir, Manifest);
            if (!File.Exists(manifest)) continue;
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                problems.Add($"{name}: a link to another folder isn't loaded");
                continue;
            }
            var id = name.ToLowerInvariant();
            if (!IdPattern().IsMatch(id))
            {
                problems.Add($"{name}: the folder's name must be letters, digits and hyphens (it becomes the plugin's address)");
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = doc.RootElement;
                var title = Str(root, "title") is { Length: > 0 } t ? t.Trim() : name;
                var entry = (Str(root, "entry") is { Length: > 0 } en ? en : "index.html").Replace('\\', '/').TrimStart('/');
                if (!InFolder(dir, entry))
                {
                    problems.Add($"{name}: its entry page must be a file inside its folder");
                    continue;
                }
                var placement = Str(root, "placement") is { } where ? where.Trim().ToLowerInvariant() : "tab";
                if (placement is not ("tab" or "panel"))
                {
                    problems.Add($"{name}: placement must be \"tab\" or \"panel\"");
                    continue;
                }
                plugins.Add(new PluginInfo(id, title, Str(root, "description"), entry, Path.GetFullPath(dir), placement, MenuOf(root, name, problems),
                    ButtonOf(root, dir, name, problems)));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{name}: {Manifest} couldn't be read ({ex.Message})");
            }
        }
        plugins.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title));
        return (plugins, problems);
    }

    public const int MaxMenuItems = 8;

    /// <summary>The menus a plugin can add items to (see <see cref="PluginMenuItem.Where"/>).</summary>
    public static readonly IReadOnlyList<string> Menus = ["palette", "project", "session", "sessionTab"];

    /// <summary>
    /// The manifest's menu items: a label, a command and the menus it's on (the palette menu if it doesn't say), the first
    /// <see cref="MaxMenuItems"/>. An item missing its label or command, or naming no menu there is, is left out, with why.
    /// </summary>
    private static List<PluginMenuItem> MenuOf(JsonElement root, string name, List<string> problems)
    {
        if (!root.TryGetProperty("menu", out var menu) || menu.ValueKind != JsonValueKind.Array) return [];
        var items = new List<PluginMenuItem>();
        foreach (var i in menu.EnumerateArray())
        {
            var label = Str(i, "label")?.Trim();
            var command = Str(i, "command");
            if (label is not { Length: > 0 and <= 60 } || command is not { Length: > 0 and <= 200 })
            {
                problems.Add($"{name}: a menu item needs a label (up to 60 characters) and a command; one is left out");
                continue;
            }
            List<string> where = !i.TryGetProperty("where", out var w) ? ["palette"]
                : w.ValueKind == JsonValueKind.String ? [w.GetString()!]
                : w.ValueKind == JsonValueKind.Array ? w.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            var known = Menus.Where(m => where.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            if (known.Count == 0 || known.Count != where.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                problems.Add($"{name}: \"{label}\" is on {(known.Count == 0 ? "no menu there is" : "a menu there isn't")}; menus are {string.Join(", ", Menus)}");
            if (known.Count == 0) continue;
            if (items.Count == MaxMenuItems)
            {
                problems.Add($"{name}: only its first {MaxMenuItems} menu items are shown");
                break;
            }
            items.Add(new PluginMenuItem(label, command, known));
        }
        return items;
    }

    private static readonly Dictionary<string, string> IconTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".ico"] = "image/x-icon", [".gif"] = "image/gif",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp",
    };

    public const int MaxIconBytes = 64 * 1024;

    /// <summary>The manifest's title bar button, if it has one and its icon is an image file in its folder (else left out, with why).</summary>
    private static PluginButton? ButtonOf(JsonElement root, string dir, string name, List<string> problems)
    {
        if (!root.TryGetProperty("toolbar", out var t) || t.ValueKind != JsonValueKind.Object) return null;
        var icon = Str(t, "icon")?.Replace('\\', '/').TrimStart('/');
        if (icon == null || !IconTypes.TryGetValue(Path.GetExtension(icon), out var type) || !InFolder(dir, icon))
        {
            problems.Add($"{name}: its toolbar button needs an icon, an image file inside its folder ({string.Join(", ", IconTypes.Keys)})");
            return null;
        }
        var bytes = File.ReadAllBytes(Path.Combine(dir, icon));
        if (bytes.Length > MaxIconBytes)
        {
            problems.Add($"{name}: its toolbar icon is over {MaxIconBytes / 1024} KB");
            return null;
        }
        var tooltip = Str(t, "tooltip")?.Trim();
        var command = Str(t, "command");
        return new PluginButton($"data:{type};base64,{Convert.ToBase64String(bytes)}", tooltip is { Length: > 0 } ? tooltip[..Math.Min(tooltip.Length, 80)] : null,
            command is { Length: > 0 and <= 200 } ? command : null);
    }

    /// <summary>True when <paramref name="relative"/> names an existing file inside <paramref name="dir"/> (no "..", no other drive).</summary>
    private static bool InFolder(string dir, string relative)
    {
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is ".." or ".")) return false;
        var full = Path.GetFullPath(Path.Combine(dir, relative.Split('?', '#')[0]));
        return full.StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(full);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
