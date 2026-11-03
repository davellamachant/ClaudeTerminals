using LlamaTree.Plugins;

namespace LlamaTree.Hosting;

/// <summary>
/// The window that shows the app page, as <see cref="AppHost"/> needs it: WinForms + WebView2 on Windows, Photino
/// (WKWebView, WebKitGTK) on macOS and Linux. Every member is called on the window's UI thread, except
/// <see cref="RunOnUi"/>, which can be called from any thread.
/// </summary>
internal interface IHostWindow
{
    /// <summary>Sends a message to the app page (and to no other document, such as a plugin's frame).</summary>
    void PostJson(string json);

    /// <summary>Runs <paramref name="action"/> on the UI thread, later (never inline); dropped once the window has closed.</summary>
    void RunOnUi(Action action);

    /// <summary>Asks for a folder; null when cancelled.</summary>
    string? PickFolder(string title, string? initial);

    /// <summary>What a paste without text puts in a terminal: the paths of files copied in the file manager, or of an image saved from the clipboard.</summary>
    string[] ClipboardFiles();

    /// <summary>The clipboard's text, or null if there's none (or it can't be read).</summary>
    string? ClipboardText();

    /// <summary>The window's own colours (title bar, and what shows before the page paints) follow the page's theme.</summary>
    /// <param name="background">The page's background ("#rrggbb"), for a theme with its own.</param>
    void ApplyTheme(bool light, string? background);

    /// <summary>Closes the window without asking (the app then exits).</summary>
    void Close();

    /// <summary>The address <paramref name="pluginId"/>'s folder is served at, ending in '/'.</summary>
    string PluginBase(string pluginId);

    /// <summary>Serves the plugin's folder at <see cref="PluginBase"/> (again, if its folder changed).</summary>
    void ServePlugin(PluginInfo plugin);

    void StopServingPlugin(string pluginId);
}
