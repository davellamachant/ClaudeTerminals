using System.Security.Cryptography;
using System.Text;
using LlamaTree.Plugins;

namespace LlamaTree.Hosting.Link;

/// <summary>
/// How the window serves plugins, which the session host needs to give each plugin its address. The Windows window
/// gives each its own virtual host (https://&lt;id&gt;.plugin.local/); the Photino window serves them from its local site
/// at a path made from this run's key, so only the app page (which frames them) knows where each one is.
/// <para>
/// A --remote window serves a server's plugins under ids of their own (see <see cref="RemoteId"/>): the window is on
/// this computer, in the same web view profile as this computer's windows, so a server's plugin must never get the
/// address of a plugin here that has the same id.
/// </para>
/// </summary>
/// <param name="Origin">The Photino window's local site (http://127.0.0.1:port), or null for the Windows window.</param>
/// <param name="Key">This run's key (hex), with <paramref name="Origin"/>.</param>
/// <param name="Remote">The server (as the --remote window names it) whose plugins these are, or null for this computer's.</param>
internal sealed record PluginAddressing(string? Origin, string? Key, string? Remote = null)
{
    public static PluginAddressing VirtualHosts { get; } = new(null, null);

    /// <summary>The id the window serves the plugin under: its own, or a server's plugin's id in a --remote window.</summary>
    public string ServedId(string pluginId) => Remote == null ? pluginId : RemoteId(Remote, pluginId);

    /// <summary>The plugin's address, ending in '/'.</summary>
    public string Base(string pluginId)
    {
        var id = ServedId(pluginId);
        return Origin == null || Key == null ? $"https://{PluginCatalog.HostFor(id)}/" : $"{Origin}/plugin/{Segment(Key, id)}/";
    }

    /// <summary>The plugin's path segment: unguessable without the key, and the same each time for the same plugin.</summary>
    public static string Segment(string key, string pluginId) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromHexString(key), Encoding.UTF8.GetBytes(pluginId)))[..32];

    /// <summary>
    /// A server's plugin's id in a --remote window: "r-" and 48 hex digits of a hash of the server (however its letters
    /// are cased) and its id there. A plugin here is named by its folder, at most 40 characters (see PluginCatalog), and a
    /// connected server's plugin in this computer's window is "s-…" (see AppHost.ServerPluginId), so it never takes
    /// either's address; it's still a valid host name label, for the Windows window's virtual host.
    /// </summary>
    public static string RemoteId(string remote, string pluginId) =>
        "r-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(remote.ToLowerInvariant() + "\n" + pluginId)))[..48];
}
