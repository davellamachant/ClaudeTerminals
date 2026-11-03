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
internal sealed class PhotinoHost : IHostWindow
{
    private readonly PhotinoWindow window;
    private readonly LocalSite site;
    private readonly AppHost app;
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
            Title = AppHost.Title,
            UseOsDefaultSize = false,
            Size = new System.Drawing.Size(1600, 1000),
            Centered = true,
            Maximized = true,
            Resizable = true,
            ContextMenuEnabled = false,
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
        app = new AppHost(this);

        // Logging out, shutting down or a kill ends the app without closing the window: the sessions still note the
        // conversations they're in (as closing does), so they reopen there.
        if (!OperatingSystem.IsWindows())
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
                signals.Add(PosixSignalRegistration.Create(signal, e =>
                {
                    e.Cancel = true;   // close the window first, then exit as usual
                    try { window.Invoke(Close); }
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
        app.Receive(message);
    }

    /// <summary>True to keep the window open.</summary>
    private bool OnClosing()
    {
        if (!closed && app.CloseQuestion() is { } question &&
            window.ShowMessage(AppHost.Title, question, PhotinoDialogButtons.OkCancel, PhotinoDialogIcon.Question) != PhotinoDialogResult.Ok)
            return true;
        ShutDownOnce();
        return false;
    }

    /// <summary>Stops the sessions once, whichever way the app ends (on the window's thread, except at process exit).</summary>
    private void ShutDownOnce()
    {
        if (Interlocked.Exchange(ref shutDown, 1) != 0) return;
        closed = true;
        app.Shutdown();
        foreach (var s in signals) s.Dispose();
    }

    // ---------------------------------------------------------------- IHostWindow

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

    public string? ClipboardText() => OperatingSystem.IsWindows() ? null : Desktop.UnixClipboardText();

    /// <summary>The page paints the whole window, and Photino leaves the title bar to the system's light or dark.</summary>
    public void ApplyTheme(bool light, string? background) { }

    public void Close()
    {
        ShutDownOnce();
        window.Close();
    }

    public string PluginBase(string pluginId) => site.PluginBase(pluginId);

    public void ServePlugin(PluginInfo plugin) => site.ServePlugin(plugin.Id, plugin.Folder);

    public void StopServingPlugin(string pluginId) => site.StopServingPlugin(pluginId);

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
