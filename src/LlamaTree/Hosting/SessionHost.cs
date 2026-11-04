using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using LlamaTree.Hosting.Link;
using LlamaTree.Plugins;

namespace LlamaTree.Hosting;

/// <summary>
/// The session host: LlamaTree started with --host, with no window of its own. It runs <see cref="AppHost"/> (the
/// sessions, git, conversations, Claude's panels and messages between sessions), so sessions carry on while the window
/// is closed. The window attaches through <see cref="HostEndpoint"/>, and is only a view of it.
/// <para>
/// It ends when nothing needs it: the window has gone and no session is running, or the app was told to quit (closing
/// with "Stop sessions", or an update). One runs per profile.
/// </para>
/// </summary>
internal sealed class SessionHost : IHostWindow
{
    /// <summary>Held while a host runs; the installer checks it too (AppMutex), so it never replaces a running host.</summary>
    public const string MutexName = "LlamaTree.Host";

    /// <summary>How long a host waits for its first window, and lingers once nothing needs it (a window may be reopening).</summary>
    private static readonly TimeSpan FirstWindowWait = TimeSpan.FromSeconds(30), Linger = TimeSpan.FromSeconds(5);

    /// <summary>How often a host checks that its socket file is still there (macOS and Linux).</summary>
    private static readonly TimeSpan SocketCheck = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<Action> queue = new();
    private readonly ManualResetEventSlim ended = new();
    private readonly Context context;
    private AppHost app = null!;
    private LinkChannel? window;
    private PluginAddressing plugins = PluginAddressing.VirtualHosts;
    private int? windowPid;
    private bool quitting;
    private bool everAttached;
    private int idleCheck;

    private SessionHost() => context = new Context(this);

    /// <summary>Runs the host until it ends. Returns at once if this profile's host is already running.</summary>
    public static int Run()
    {
        using var mutex = InstanceLock.Take(MutexName + AppProfile.InstanceSuffix, out var first);
        if (!first) return 0;
        var signals = new List<PosixSignalRegistration>();
        if (!OperatingSystem.IsWindows())
        {
            // Not ended with the terminal or window that started it: a hang-up is ignored, and an ordinary request to
            // end (logging out, shutting down) stops the sessions properly, each noting its conversation.
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, e => e.Cancel = true));
        }
        AppProfile.MoveFromOldName();
        var host = new SessionHost();
        if (!OperatingSystem.IsWindows())
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, e => { e.Cancel = true; host.RunOnUi(host.Quit); }));
        var thread = new Thread(host.Loop) { Name = "LlamaTree host", IsBackground = false };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);   // for opening files with the shell
        thread.Start();
        host.RunOnUi(host.Begin);
        host.ended.Wait();
        foreach (var s in signals) s.Dispose();
        return 0;
    }

    private void Loop()
    {
        SynchronizationContext.SetSynchronizationContext(context);
        foreach (var action in queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception) { /* as an exception in a window's BeginInvoke would, it mustn't stop the rest */ }
        }
    }

    private void Begin()
    {
        app = new AppHost(this);
        app.RunningChanged += CheckIdle;
        app.QuitRequested += Quit;
        _ = AcceptLoopAsync();
        // Started for a window that then never came (it failed, or was closed at once): don't stay.
        _ = Task.Delay(FirstWindowWait).ContinueWith(_ => RunOnUi(() => { if (!everAttached) CheckIdle(); }));
    }

    private async Task AcceptLoopAsync()
    {
        while (!quitting)
        {
            System.IO.Pipes.NamedPipeServerStream server;
            try
            {
                server = HostEndpoint.Listen();
                var waiting = server.WaitForConnectionAsync();
                // On macOS and Linux the socket is a file, which can be removed under a running host: Linux empties the
                // runtime folder when the user's last login ends (an SSH session, say), and old files in /tmp get
                // tidied. Then it's listened for again, so the next window still finds this host.
                var gone = false;
                while (await Task.WhenAny(waiting, Task.Delay(SocketCheck)) != waiting)
                {
                    if (HostEndpoint.IsListening) continue;
                    gone = true;
                    break;
                }
                if (gone)
                {
                    _ = waiting.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);   // ends once the stream is disposed
                    await server.DisposeAsync();
                    continue;
                }
                await waiting;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                await Task.Delay(500);   // e.g. the socket's folder went away for a moment
                continue;
            }
            var channel = new LinkChannel(server);
            channel.Received += (kind, json) => RunOnUi(() => OnMessage(channel, kind, json));
            channel.Closed += () => RunOnUi(() => OnClosed(channel));
            channel.Start();
        }
    }

    private void OnMessage(LinkChannel channel, char kind, string json)
    {
        if (kind == LinkKind.Hello)
        {
            // A new window takes over from one still attached (only one shows the sessions at a time).
            if (window != null && window != channel)
            {
                var old = window;
                old.Send(LinkKind.Window, """{"op":"close"}""");
                _ = old.FlushAndCloseAsync(TimeSpan.FromSeconds(2));
            }
            window = channel;
            everAttached = true;
            ReadHello(json);
            // Which LlamaTree this is: a window on another computer may be a different version.
            channel.Send(LinkKind.Window, JsonSerializer.Serialize(new
            {
                op = "welcome", version = Updates.UpdateService.CurrentVersion.ToString(), machine = Environment.MachineName,
            }));
            app.Attached();
        }
        else if (kind == LinkKind.Page && channel == window)
        {
            app.Receive(json);
        }
    }

    private void ReadHello(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Null for a window on another computer (TryGetInt32 throws on anything but a number).
            windowPid = root.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var p) ? p : null;
            app.Remote = root.TryGetProperty("remote", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            plugins = root.TryGetProperty("plugins", out var pl) && pl.ValueKind == JsonValueKind.Object &&
                      pl.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.String &&
                      pl.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                ? new PluginAddressing(o.GetString(), k.GetString())
                : PluginAddressing.VirtualHosts;
        }
        catch (JsonException)
        {
            (windowPid, plugins, app.Remote) = (null, PluginAddressing.VirtualHosts, null);
        }
    }

    /// <summary>
    /// Starts this profile's session host (this program, with --host), detached so it outlives what started it: a
    /// window, or an SSH connection's bridge (see <see cref="HostBridge"/>). On macOS and Linux it's left no terminal or
    /// SSH channel to hold on to, so the connection can end without waiting for it.
    /// </summary>
    public static bool StartDetached()
    {
        var unix = !OperatingSystem.IsWindows();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No program path."))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardInput = unix,
                RedirectStandardOutput = unix,
                RedirectStandardError = unix,
            };
            psi.ArgumentList.Add("--host");
            using var host = System.Diagnostics.Process.Start(psi);
            if (host != null && unix)
            {
                // Nothing reads them: closed, so the host's input is empty and anything it writes goes nowhere.
                host.StandardInput.Close();
                host.StandardOutput.Close();
                host.StandardError.Close();
            }
            return host != null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private void OnClosed(LinkChannel channel)
    {
        if (channel != window) return;
        window = null;
        windowPid = null;
        app.Detached();
        CheckIdle();
    }

    /// <summary>
    /// Ends the host once nothing needs it: no window, and no session running (or the app was told to quit). It waits a
    /// moment first, in case a window is reopening or a session restarting.
    /// </summary>
    private void CheckIdle()
    {
        if (window != null || (!quitting && app.AnyRunning)) return;
        var check = ++idleCheck;
        _ = Task.Delay(Linger).ContinueWith(_ => RunOnUi(() =>
        {
            if (check == idleCheck && window == null && (quitting || !app.AnyRunning)) End();
        }));
    }

    /// <summary>The app was told to quit: once the window has gone (it was told to close), the host ends, stopping the sessions.</summary>
    private void Quit()
    {
        quitting = true;
        if (window == null) End();
        else _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => RunOnUi(End));   // a window that doesn't close
    }

    private void End()
    {
        if (ended.IsSet) return;
        quitting = true;
        app.Shutdown();
        window?.Close();
        ended.Set();
        queue.CompleteAdding();
    }

    // ---------------------------------------------------------------- IHostWindow

    public void PostJson(string json) => window?.Send(LinkKind.Page, json);

    public void RunOnUi(Action action)
    {
        try { queue.Add(action); }
        catch (InvalidOperationException) { /* ended */ }
    }

    public int? ProcessId => windowPid;

    public void Close() => window?.Send(LinkKind.Window, """{"op":"close"}""");

    public string PluginBase(string pluginId) => plugins.Base(pluginId);

    public void ServePlugin(PluginInfo plugin) =>
        window?.Send(LinkKind.Window, JsonSerializer.Serialize(new { op = "serve", id = plugin.Id, folder = plugin.Folder }));

    public void StopServingPlugin(string pluginId) =>
        window?.Send(LinkKind.Window, JsonSerializer.Serialize(new { op = "unserve", id = pluginId }));

    /// <summary>Continuations of the app's awaits run back on the host's app thread.</summary>
    private sealed class Context(SessionHost host) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => host.RunOnUi(() => d(state));

        public override SynchronizationContext CreateCopy() => this;
    }
}
