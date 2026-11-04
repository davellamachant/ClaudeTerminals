using LlamaTree.Plugins;

namespace LlamaTree.Hosting;

/// <summary>
/// The window showing the app page, as <see cref="AppHost"/> sees it from the session host (see
/// <see cref="SessionHost"/>): there may be none attached, and one can come and go while sessions run. What only the
/// window can do itself (the folder picker, the clipboard, the title bar's colours) it does without the host (see
/// <see cref="HostLink"/>). Every member is called on the host's app thread, except <see cref="RunOnUi"/>, which can be
/// called from any thread.
/// </summary>
internal interface IHostWindow
{
    /// <summary>Sends a message to the app page, if a window is attached.</summary>
    void PostJson(string json);

    /// <summary>Runs <paramref name="action"/> on the app thread, later (never inline).</summary>
    void RunOnUi(Action action);

    /// <summary>The attached window's process, which an update waits for as well; null when none is attached.</summary>
    int? ProcessId { get; }

    /// <summary>Closes the attached window without asking.</summary>
    void Close();

    /// <summary>The address <paramref name="pluginId"/>'s folder is served at, ending in '/'.</summary>
    string PluginBase(string pluginId);

    /// <summary>Has the window serve the plugin's folder at <see cref="PluginBase"/> (again, if its folder changed).</summary>
    void ServePlugin(PluginInfo plugin);

    void StopServingPlugin(string pluginId);

    /// <summary>
    /// Gives a window on another computer a copy of the file, which it opens there (see
    /// <see cref="Platform.Desktop.OpenFileForViewing"/>). Reads the file on the calling thread; any thread.
    /// </summary>
    void SendFile(string path);
}
