using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LlamaTree;

static class Program
{
    /// <summary>Also referenced by the installer (AppMutex) to detect a running copy.</summary>
    private const string MutexName = "LlamaTree.SingleInstance";

    [STAThread]
    static void Main()
    {
        // A second copy would start every session again; bring the existing window forward instead.
        // A separate profile (LLAMATREE_DATA) has its own sessions, so it gets its own lock.
        var profile = AppProfile.CustomFolder;
        var name = string.IsNullOrEmpty(profile)
            ? MutexName
            : MutexName + "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile.ToUpperInvariant())))[..12];
        using var mutex = new Mutex(true, name, out var isFirst);
        if (!isFirst)
        {
            ActivateExistingInstance();
            return;
        }

        AppProfile.MoveFromOldName();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

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
