using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlamaTree.Platform;
using LlamaTree.Plugins;
using Photino.NET;

namespace LlamaTree.Hosting;

/// <summary>
/// The macOS and Linux window (it runs on Windows too, for trying it out): the app page in a Photino window, which
/// hosts WKWebView on macOS, WebKitGTK on Linux and WebView2 on Windows. The page and plugins come from
/// <see cref="LocalSite"/>.
/// <para>
/// The message bridge is reachable from every frame in the page, plugins' included, so it trusts a message only when
/// it carries this run's bridge token. The token is written into the app page itself as <see cref="LocalSite"/> serves
/// it, at an address made for this run: only a script running in that page can read it. A plugin's frame is sandboxed
/// on an opaque origin, so it can't read the page, and a page from anywhere else can't fetch it (no CORS); nothing is
/// ever handed the token by asking.
/// </para>
/// </summary>
internal sealed class PhotinoHost : HostLink.IWindow
{
    private readonly PhotinoWindow window;
    private readonly LocalSite site;
    private readonly HostLink link;
    private readonly string bridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly ConcurrentQueue<Action> queue = new();
    private readonly UiContext context;
    private readonly List<PosixSignalRegistration> signals = [];
    private int pumpQueued;
    private int shutDown;
    private volatile bool created;
    private volatile bool closed;
    /// <summary>Set once the page has sent its first message: the web view is there to take messages (sent before then, Photino crashes).</summary>
    private bool pageUp;

    public static void Run()
    {
        var host = new PhotinoHost();
        host.window.WaitForClose();
        host.ShutDownOnce();
        host.site.Dispose();
    }

    private PhotinoHost()
    {
        context = new UiContext(this);
        site = LocalSite.Start(Path.Combine(AppContext.BaseDirectory, "wwwroot"), bridgeToken);
        window = new PhotinoWindow
        {
            Title = HostLink.WindowTitle,
            UseOsDefaultSize = false,
            Size = new System.Drawing.Size(1600, 1000),
            Centered = true,
            Maximized = true,
            Resizable = true,
            ContextMenuEnabled = false,
            // On, so copying keeps working in WKWebView and WebKitGTK. It's for the whole web view, plugins' frames too:
            // there they rely on the frame's sandbox (an opaque origin) to keep them from the clipboard, and on the
            // Permissions-Policy the local site serves them with (see LocalSite.PluginPermissions).
            JavascriptClipboardAccessEnabled = true,
            // The page sits on its own loopback address; it never needs other browser permissions without asking.
            GrantBrowserPermissions = false,
#if DEBUG
            DevToolsEnabled = true,
#else
            DevToolsEnabled = false,
#endif
            LogVerbosity = 0,
        };
        var icon = Path.Combine(AppContext.BaseDirectory, "app.png");
        if (!OperatingSystem.IsWindows() && File.Exists(icon)) window.IconFile = icon;
        if (OperatingSystem.IsWindows())   // WebView2 keeps its data here, apart from the Windows build's
            window.TemporaryFilesPath = Path.Combine(AppProfile.CustomFolder != null ? Sessions.SessionStore.AppDataFolder : AppProfile.LocalFolder, "Photino");
        window.RegisterWindowCreatedHandler((_, _) =>
        {
            created = true;
            QueuePump();   // anything sessions sent before the window was there
        });
        window.RegisterWebMessageReceivedHandler((_, message) => OnMessage(message));
        window.RegisterWindowClosingHandler((_, _) => OnClosing());
        link = new HostLink(this);

        // Logging out, shutting down or a kill closes the window without asking; the sessions are the host's, which
        // ends them properly itself when it's told to.
        if (!OperatingSystem.IsWindows())
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
                signals.Add(PosixSignalRegistration.Create(signal, e =>
                {
                    e.Cancel = true;   // close the window first, then exit as usual
                    try { window.Invoke(CloseNow); }
                    catch (Exception) { ShutDownOnce(); Environment.Exit(0); }
                }));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutDownOnce();   // e.g. Quit from the Dock or menu bar
        window.Load(site.AppPage);
    }

    // ---------------------------------------------------------------- bridge

    private void OnMessage(string message)
    {
        SynchronizationContext.SetSynchronizationContext(context);   // so the app's awaits come back to this thread
        string? token;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            token = root.TryGetProperty("bridgeToken", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        }
        catch (JsonException)
        {
            return;
        }
        if (token == null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(bridgeToken))) return;
        pageUp = true;
        link.Receive(message);
    }

    /// <summary>True to keep the window open: the page is asking about running sessions (see <see cref="HostLink.AskToClose"/>).</summary>
    private bool OnClosing()
    {
        if (!closed && !link.AskToClose()) return true;
        ShutDownOnce();
        return false;
    }

    /// <summary>Lets the host know the window has gone, once, whichever way the window ends.</summary>
    private void ShutDownOnce()
    {
        if (Interlocked.Exchange(ref shutDown, 1) != 0) return;
        closed = true;
        link.Detach();
        foreach (var s in signals) s.Dispose();
    }

    // ---------------------------------------------------------------- HostLink.IWindow

    public void PostJson(string json)
    {
        if (pageUp && !closed) window.SendWebMessage(json);
    }

    /// <summary>
    /// Queued, and run by one call into the window's thread for everything waiting (Photino's Invoke runs its work
    /// straight away when it's already on that thread, which would make this run inline).
    /// </summary>
    public void RunOnUi(Action action)
    {
        if (closed) return;
        queue.Enqueue(action);
        QueuePump();
    }

    private void QueuePump()
    {
        if (!created || closed || Interlocked.Exchange(ref pumpQueued, 1) == 1) return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { window.Invoke(Pump); }
            catch (Exception) { Volatile.Write(ref pumpQueued, 0); }   // the window is going
        }, null);
    }

    private void Pump()
    {
        SynchronizationContext.SetSynchronizationContext(context);
        Volatile.Write(ref pumpQueued, 0);
        // Only what's queued now: more queued by these actions goes in the next round, so the window keeps painting.
        for (var n = queue.Count; n > 0 && !closed && queue.TryDequeue(out var action); n--)
        {
            try { action(); }
            catch (Exception) { /* as an exception in a WinForms BeginInvoke would, it mustn't stop the rest */ }
        }
        if (!queue.IsEmpty) QueuePump();
    }

    public string? PickFolder(string title, string? initial)
    {
        var picked = window.ShowOpenFolder(title, initial, false);
        return picked is { Length: > 0 } ? picked[0] : null;
    }

    public string[] ClipboardFiles() => OperatingSystem.IsWindows() ? [] : Desktop.UnixClipboardFiles();

    /// <summary>WKWebView and WebKitGTK give the page a dropped file's contents but never its path: the page sends those.</summary>
    public string[] DroppedFiles() => [];

    /// <summary>Photino can't bring its window to the front; the window manager decides whether a drop does.</summary>
    public void BringToFront() { }

    public string? ClipboardText() => OperatingSystem.IsWindows() ? null : Desktop.UnixClipboardText();

    /// <summary>The page paints the whole window, and Photino leaves the title bar to the system's light or dark.</summary>
    public void ApplyTheme(bool light, string? background) { }

    public void CloseNow()
    {
        ShutDownOnce();
        window.Close();
    }

    /// <summary>The page again, from the top (it gets the bridge token anew).</summary>
    public void ReloadPage()
    {
        pageUp = false;
        window.Load(site.AppPage);
    }

    public Link.PluginAddressing Plugins => new(site.Origin, site.PluginKey);

    public void ServePlugin(string id, string folder) => site.ServePlugin(id, folder);

    public void StopServingPlugin(string id) => site.StopServingPlugin(id);

    /// <summary>Continuations of the app's awaits run back on the window's thread, as they do in WinForms.</summary>
    private sealed class UiContext(PhotinoHost host) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // Not dropped while closing, unlike other work: an await in progress finishes (its result just goes nowhere).
            host.queue.Enqueue(() => d(state));
            host.QueuePump();
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
