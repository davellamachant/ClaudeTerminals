using LlamaTree.Hosting;
using LlamaTree.Platform;
using LlamaTree.Plugins;
using LlamaTree.Sessions;
using LlamaTree.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LlamaTree;

/// <summary>
/// The Windows window: the app page in a WebView2, its messages handed to <see cref="AppHost"/>. The page and plugins
/// are served from folders as virtual hosts (https://app.local/, https://&lt;id&gt;.plugin.local/).
/// </summary>
internal sealed class MainForm : Form, IHostWindow
{
    /// <summary>The only document allowed in the WebView, and the only one whose messages the host acts on.</summary>
    private const string AppPage = "https://app.local/index.html";

    private readonly WebView2 webView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x1f, 0x1f, 0x1f) };
    private readonly AppHost app;

    public MainForm()
    {
        Text = AppHost.Title;   // shown in the taskbar and Alt+Tab
        BackColor = Color.FromArgb(0x1f, 0x1f, 0x1f);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1600, 1000);
        WindowState = FormWindowState.Maximized;
        var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (icon != null) Icon = icon;
        Controls.Add(webView);
        CreateHandle();   // so RunOnUi can marshal to this thread from the start
        app = new AppHost(this);
    }

    // ---------------------------------------------------------------- IHostWindow

    public void PostJson(string json)
    {
        if (webView.CoreWebView2 is not { } core || !IsAppUri(core.Source)) return;
        core.PostWebMessageAsJson(json);
    }

    public void RunOnUi(Action action)
    {
        if (IsDisposed || Disposing) return;
        try { BeginInvoke(action); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { /* closing */ }
    }

    public string? PickFolder(string title, string? initial)
    {
        using var dlg = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        if (initial != null) dlg.InitialDirectory = initial;
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    /// <summary>
    /// The files copied in Explorer, or the image on the clipboard (a screenshot, a copied picture) saved as a PNG in the
    /// temp folder.
    /// </summary>
    public string[] ClipboardFiles()
    {
        if (Clipboard.ContainsFileDropList()) return Clipboard.GetFileDropList().Cast<string>().ToArray();
        if (!Clipboard.ContainsImage()) return [];
        var path = Desktop.NewPastedImagePath();
        // The PNG as the app that copied it wrote it (keeping transparency), else the bitmap.
        if (Clipboard.GetData("PNG") is MemoryStream png) File.WriteAllBytes(path, png.ToArray());
        else if (Clipboard.GetImage() is { } image) using (image) image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        else return [];
        return [path];
    }

    public string? ClipboardText() => Clipboard.ContainsText() ? Clipboard.GetText() : null;

    public void ApplyTheme(bool light, string? background)
    {
        var dark = light ? 0 : 1;
        Native.DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        var own = background is { Length: 7 } hex && hex[0] == '#' &&
            int.TryParse(hex[1..], System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? Color.FromArgb(rgb >> 16 & 0xff, rgb >> 8 & 0xff, rgb & 0xff)
            : (Color?)null;
        // Such a theme's title bar takes its colour too (Windows 11; earlier versions keep the dark one).
        var caption = own is { } c ? ColorTranslator.ToWin32(c) : unchecked((int)0xFFFFFFFF) /* DWMWA_COLOR_DEFAULT */;
        Native.DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        var color = own ?? (light ? Color.FromArgb(0xee, 0xee, 0xee) : Color.FromArgb(0x1f, 0x1f, 0x1f));
        webView.DefaultBackgroundColor = color;
        BackColor = color;
    }

    /// <summary>https://&lt;id&gt;.plugin.local/: every plugin has an origin of its own, apart from the app's and each other's.</summary>
    public string PluginBase(string pluginId) => $"https://{PluginCatalog.HostFor(pluginId)}/";

    /// <summary>Reads from other origins are refused (DenyCors), so neither another plugin nor a page elsewhere can fetch its files.</summary>
    public void ServePlugin(PluginInfo plugin) =>
        webView.CoreWebView2?.SetVirtualHostNameToFolderMapping(plugin.Host, plugin.Folder, CoreWebView2HostResourceAccessKind.DenyCors);

    public void StopServingPlugin(string pluginId) => webView.CoreWebView2?.ClearVirtualHostNameToFolderMapping(PluginCatalog.HostFor(pluginId));

    // ---------------------------------------------------------------- window

    /// <summary>True for the app page itself (ignoring any query or fragment); false for every other document, including others under app.local.</summary>
    internal static bool IsAppUri(string? uri) =>
        uri != null && string.Equals(uri.Split('#', '?')[0], AppPage, StringComparison.OrdinalIgnoreCase);

    private System.Windows.Forms.Timer? accentTimer;

    /// <summary>Follows the Windows accent colour when it's changed in Settings.</summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        const int WM_SETTINGCHANGE = 0x001A, WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
        var accentChanged = m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED ||
            (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero &&
             System.Runtime.InteropServices.Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet");
        if (!accentChanged || app is not { WebReady: true }) return;
        // Windows sends several of these, and may write the new shades just after: read once things settle.
        if (accentTimer == null)
        {
            accentTimer = new System.Windows.Forms.Timer { Interval = 400 };
            accentTimer.Tick += (_, _) => { accentTimer.Stop(); app.PostAccent(); };
        }
        accentTimer.Stop();
        accentTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var on = 1;
        Native.DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            // A custom profile (LLAMATREE_DATA) keeps its browser data with it, so it never shares a browser process with the default one.
            var userData = AppProfile.CustomFolder != null
                ? Path.Combine(SessionStore.AppDataFolder, "WebView2")
                : Path.Combine(AppProfile.LocalFolder, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await webView.EnsureCoreWebView2Async(env);
            var core = webView.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            // A file or link dropped on the window would otherwise replace the app page (and could then talk to the host).
            webView.AllowExternalDrop = false;
            core.NavigationStarting += (_, args) => { if (!IsAppUri(args.Uri)) args.Cancel = true; };
            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping("app.local", root, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, args) => { if (IsAppUri(args.Source)) app.Receive(args.WebMessageAsJson); };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            // Plugins are pages in frames of their own (see PluginCatalog): a plugin's frame stays on plugin addresses.
            core.FrameNavigationStarting += (_, args) => { if (!PluginCatalog.IsPluginUri(args.Uri) && args.Uri != "about:blank") args.Cancel = true; };
            // And on its own plugin's address: the page answers a plugin's frame with what that plugin was allowed to see,
            // so another plugin's page mustn't get into it.
            core.FrameCreated += (_, created) =>
            {
                string? host = null;
                created.Frame.NavigationStarting += (_, args) =>
                {
                    if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
                    host ??= u.Host;
                    if (!string.Equals(host, u.Host, StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
                };
            };
            // The terminal's Paste menu item reads the clipboard; without this WebView2 asks "Allow clipboard access?"
            // the first time, a prompt that also takes focus away from the terminal. Only the app's own origin gets it
            // (requests name the origin, not the page; navigation never leaves the app page anyway).
            core.PermissionRequested += (_, args) =>
            {
                if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead &&
                    Uri.TryCreate(args.Uri, UriKind.Absolute, out var origin) &&
                    origin.Scheme == Uri.UriSchemeHttps && string.Equals(origin.Host, "app.local", StringComparison.OrdinalIgnoreCase))
                    args.State = CoreWebView2PermissionState.Allow;
                // A plugin gets nothing: no clipboard, camera, location or notifications, and no prompt asking for them.
                else if (PluginCatalog.IsPluginUri(args.Uri))
                    args.State = CoreWebView2PermissionState.Deny;
            };
            // The page's files come from the install folder and only change when the app is updated, but the browser
            // caches them heuristically from their timestamps; after an upgrade it could keep running the previous
            // version's app.js, possibly mixed with new files. Start each launch with an empty HTTP cache.
            try { await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}"); }
            catch (Exception) { /* best effort: worst case is the old behaviour */ }
            core.Navigate(AppPage);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to initialise WebView2:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && app.CloseQuestion() is { } question &&
            MessageBox.Show(this, question, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            e.Cancel = true;
            return;
        }
        app.Shutdown();
        base.OnFormClosing(e);
    }
}
