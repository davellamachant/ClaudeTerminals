using LlamaTree.Hosting;
using LlamaTree.Platform;
using LlamaTree.Plugins;
using LlamaTree.Sessions;
using LlamaTree.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LlamaTree;

/// <summary>
/// The Windows window: the app page in a WebView2, its messages passed to the session host (see <see cref="HostLink"/>).
/// The page and plugins are served from folders as virtual hosts (https://app.local/, https://&lt;id&gt;.plugin.local/).
/// </summary>
internal sealed class MainForm : Form, HostLink.IWindow
{
    /// <summary>The only document allowed in the WebView, and the only one whose messages are passed on.</summary>
    private const string AppPage = "https://app.local/index.html";

    private readonly WebView2 webView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x1f, 0x1f, 0x1f) };
    private readonly HostLink link;
    private bool pageUp;

    public MainForm()
    {
        Text = HostLink.WindowTitle;   // shown in the taskbar and Alt+Tab
        BackColor = Color.FromArgb(0x1f, 0x1f, 0x1f);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1600, 1000);
        WindowState = FormWindowState.Maximized;
        var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (icon != null) Icon = icon;
        Controls.Add(webView);
        CreateHandle();   // so RunOnUi can marshal to this thread from the start
        link = new HostLink(this);
    }

    // ---------------------------------------------------------------- HostLink.IWindow

    public void PostJson(string json)
    {
        if (webView.CoreWebView2 is not { } core || !IsAppUri(core.Source)) return;
        core.PostWebMessageAsJson(json);
    }

    public Hosting.Link.PluginAddressing Plugins => Hosting.Link.PluginAddressing.VirtualHosts;

    public void CloseNow() => Close();

    public void ReloadPage() => webView.CoreWebView2?.Navigate(AppPage);

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
    /// app's own folder (see <see cref="Desktop.NewPastedImagePath()"/>).
    /// </summary>
    public string[] ClipboardFiles()
    {
        if (Clipboard.ContainsFileDropList()) return Clipboard.GetFileDropList().Cast<string>().ToArray();
        if (!Clipboard.ContainsImage()) return [];
        // The PNG as the app that copied it wrote it (keeping transparency), else the bitmap.
        using var png = Clipboard.GetData("PNG") as MemoryStream;
        using var image = png == null ? Clipboard.GetImage() : null;
        if (png == null && image == null) return [];
        var path = Desktop.NewPastedImagePath();
        using (var file = Desktop.CreatePastedImage(path))
        {
            if (png != null) png.WriteTo(file);
            else image!.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        return [path];
    }

    public string? ClipboardText() => Clipboard.ContainsText() ? Clipboard.GetText() : null;

    /// <summary>The files sent with the message being received (see OnLoad's WebMessageReceived).</summary>
    private string[] dropped = [];

    public string[] DroppedFiles() => dropped;

    /// <summary>
    /// Comes to the front after files are dropped on it, so typing goes on in the terminal. The drop leaves the app they
    /// were dragged from in front, and Windows won't let another app take that place by asking: it's joined to that
    /// app's input for the moment it takes it.
    /// </summary>
    public void BringToFront()
    {
        if (WindowState == FormWindowState.Minimized) return;
        var front = Native.GetForegroundWindow();
        if (front == Handle) return;
        var theirs = Native.GetWindowThreadProcessId(front, IntPtr.Zero);
        var ours = Native.GetCurrentThreadId();
        var joined = theirs != 0 && theirs != ours && Native.AttachThreadInput(ours, theirs, true);
        try
        {
            Native.SetForegroundWindow(Handle);
            Activate();
        }
        finally
        {
            if (joined) Native.AttachThreadInput(ours, theirs, false);
        }
        webView.Focus();
    }

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

    /// <summary>
    /// At https://&lt;id&gt;.plugin.local/: every plugin has an origin of its own, apart from the app's and each other's.
    /// Reads from other origins are refused (DenyCors), so neither another plugin nor a page elsewhere can fetch its files.
    /// Asked before the WebView is up (a host already running), it's done once the WebView is.
    /// </summary>
    public void ServePlugin(string id, string folder)
    {
        servedPlugins[id] = folder;
        webView.CoreWebView2?.SetVirtualHostNameToFolderMapping(PluginCatalog.HostFor(id), folder, CoreWebView2HostResourceAccessKind.DenyCors);
    }

    public void StopServingPlugin(string id)
    {
        servedPlugins.Remove(id);
        webView.CoreWebView2?.ClearVirtualHostNameToFolderMapping(PluginCatalog.HostFor(id));
    }

    private readonly Dictionary<string, string> servedPlugins = [];

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
        if (!accentChanged || !pageUp) return;
        // Windows sends several of these, and may write the new shades just after: read once things settle.
        if (accentTimer == null)
        {
            accentTimer = new System.Windows.Forms.Timer { Interval = 400 };
            accentTimer.Tick += (_, _) => { accentTimer.Stop(); PostJson(AppHost.AccentMessage()); };
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
            // Files dropped on a terminal paste their paths (see DroppedFiles). A file or link dropped anywhere else would
            // navigate to it, which the page prevents, and failing that the check below: nothing but the app page loads.
            webView.AllowExternalDrop = true;
            core.NavigationStarting += (_, args) => { if (!IsAppUri(args.Uri)) args.Cancel = true; };
            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping("app.local", root, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, args) =>
            {
                if (!IsAppUri(args.Source)) return;
                pageUp = true;
                // Files the page sent along (postMessageWithAdditionalObjects): the dropped files, by their paths here.
                dropped = args.AdditionalObjects is { Count: > 0 } objects
                    ? objects.OfType<CoreWebView2File>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray()
                    : [];
                try { link.Receive(args.WebMessageAsJson); }
                finally { dropped = []; }
            };
            foreach (var (id, folder) in servedPlugins)
                core.SetVirtualHostNameToFolderMapping(PluginCatalog.HostFor(id), folder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            // Plugins are pages in frames of their own (see PluginCatalog): a plugin's frame stays on plugin addresses.
            // A theme's own floor is a frame of the page's own writing (srcdoc), which stays put: it has no way to navigate.
            core.FrameNavigationStarting += (_, args) => { if (!PluginCatalog.IsPluginUri(args.Uri) && args.Uri is not ("about:blank" or "about:srcdoc")) args.Cancel = true; };
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
                // A plugin's frame (or one inside it) asks here first, whatever origin it names: the answer is no, and
                // the page's own handler below never hears of it.
                created.Frame.PermissionRequested += (_, args) =>
                {
                    Answer(args, fromFrame: true);
                    args.Handled = true;
                };
            };
            // Only the app page reads the clipboard; plugins and any other origin get no permission (see WebPermissions).
            core.PermissionRequested += (_, args) => Answer(args, fromFrame: false);
            static void Answer(CoreWebView2PermissionRequestedEventArgs args, bool fromFrame)
            {
                var answer = WebPermissions.Decide(args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead, args.Uri, fromFrame);
                if (answer != WebPermissionAnswer.Default)
                    args.State = answer == WebPermissionAnswer.Allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            }
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

    /// <summary>
    /// Closing asks the page, which asks you about running sessions (see <see cref="HostLink.AskToClose"/>); its answer
    /// closes the window. Logging off or shutting down closes it straight away, leaving the sessions to the host.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !link.AskToClose())
        {
            e.Cancel = true;
            return;
        }
        link.Detach();
        base.OnFormClosing(e);
    }
}
