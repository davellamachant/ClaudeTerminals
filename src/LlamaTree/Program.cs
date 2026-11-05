using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LlamaTree;

static class Program
{
    /// <summary>Also referenced by the installer (AppMutex) to detect a running copy.</summary>
    private const string MutexName = "LlamaTree.SingleInstance";

    [STAThread]
    static int Main(string[] args)
    {
        // The session host: the sessions themselves, without a window (started by the window; see SessionHost).
        if (args is ["--host", ..]) return Hosting.SessionHost.Run();
        // On a server, over SSH: the connection between a window elsewhere and this machine's session host (see HostBridge).
        if (args is ["--attach", ..]) return Hosting.HostBridge.Run();
        // A window showing a server's sessions: --remote user@server.
        if (args is ["--remote", var target, ..])
        {
            if (!Hosting.HostLink.IsValidTarget(target))
            {
                Console.Error.WriteLine("LlamaTree: --remote takes a server, as user@server or a Host from your SSH config.");
                return 2;
            }
            Hosting.HostLink.Remote = target;
        }

        // A second window would show the same sessions twice; bring the existing one forward instead.
        // A separate profile (LLAMATREE_DATA) has its own sessions, so it gets its own lock, as does each server.
        using var mutex = Hosting.InstanceLock.Take(MutexName + AppProfile.InstanceSuffix + RemoteSuffix(), out var isFirst);
        if (!isFirst)
        {
            if (OperatingSystem.IsWindows() && Hosting.HostLink.Remote == null) ActivateExistingInstance();
            else Console.Error.WriteLine("LlamaTree is already running" + (Hosting.HostLink.Remote is { } r ? " for " + r : "") + ".");
            return 0;
        }

        AppProfile.MoveFromOldName();
#if WINFORMS
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
#else
        Hosting.PhotinoHost.Run();
#endif
        return 0;
    }

    /// <summary>A server's window has a lock of its own (a lock's name can't hold every character a target can, so a hash of it).</summary>
    private static string RemoteSuffix() => Hosting.HostLink.Remote is { } r
        ? ".remote." + Hosting.HostLink.RemoteKey(r)
        : "";

    private static void ActivateExistingInstance()
    {
        var current = Process.GetCurrentProcess();
        var other = Process.GetProcessesByName(current.ProcessName).FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);
        if (other == null) return;
        if (IsIconic(other.MainWindowHandle)) ShowWindow(other.MainWindowHandle, 9 /* SW_RESTORE */);
        SetForegroundWindow(other.MainWindowHandle);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
