using System.Security.Cryptography;
using System.Text;
using LlamaTree.Plugins;

namespace LlamaTree.Hosting.Link;

/// <summary>
/// How the window serves plugins, which the session host needs to give each plugin its address. The Windows window
/// gives each its own virtual host (https://&lt;id&gt;.plugin.local/); the Photino window serves them from its local site
/// at a path made from this run's key, so only the app page (which frames them) knows where each one is.
/// </summary>
/// <param name="Origin">The Photino window's local site (http://127.0.0.1:port), or null for the Windows window.</param>
/// <param name="Key">This run's key (hex), with <paramref name="Origin"/>.</param>
internal sealed record PluginAddressing(string? Origin, string? Key)
{
    public static PluginAddressing VirtualHosts { get; } = new(null, null);

    /// <summary>The plugin's address, ending in '/'.</summary>
    public string Base(string pluginId) =>
        Origin == null || Key == null ? $"https://{PluginCatalog.HostFor(pluginId)}/" : $"{Origin}/plugin/{Segment(Key, pluginId)}/";

    /// <summary>The plugin's path segment: unguessable without the key, and the same each time for the same plugin.</summary>
    public static string Segment(string key, string pluginId) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromHexString(key), Encoding.UTF8.GetBytes(pluginId)))[..32];
}
