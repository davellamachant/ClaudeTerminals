using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using LlamaTree.Terminal;

namespace LlamaTree.Hosting;

/// <summary>
/// Starts the session host on a Windows server for a connection over SSH (see <see cref="HostBridge"/>), where
/// <see cref="SessionHost.StartDetached"/> won't do: Windows' OpenSSH Server runs a connection's commands in a job it
/// ends, with everything in it, when the connection closes; and under the token of an SSH sign-in, which (signed in by
/// key, with no password) can't use what Windows keeps for you behind your password (Credential Manager, where Git keeps
/// its sign-ins), and for an administrator is elevated, so the desktop's LlamaTree couldn't reach the host it made.
/// <para>
/// So the host is started in your desktop session, by a task in Task Scheduler that runs it there as you, as though
/// you'd started it. When you aren't signed in there (no desktop session to run it in), it's started outside the job
/// instead, under the SSH sign-in, which keeps the sessions running without those.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsHostStart
{
    /// <summary>The task's name: one per user (tasks are per computer) and profile.</summary>
    private static string TaskName => "LlamaTree session host" + AppProfile.InstanceSuffix;

    /// <summary>
    /// Asks Task Scheduler to start the host in your desktop session. True if it took the task; whether the host then
    /// starts shows by its answering (it doesn't when you aren't signed in there). A task doesn't carry LLAMATREE_DATA,
    /// so a separate profile's folder goes on its command line instead (--data).
    /// </summary>
    public static bool InDesktopSession()
    {
        var exe = Environment.ProcessPath;
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (exe == null || sid == null) return false;
        // LLAMATREE_HOST_START=job: never in the desktop session, for testing the start outside the job.
        if (Environment.GetEnvironmentVariable("LLAMATREE_HOST_START") == "job") return false;
        var data = AppProfile.CustomFolder?.TrimEnd('\\');   // a backslash before the closing quote would escape it
        if (data != null && data.Contains('"')) return false;
        var arguments = data is { Length: > 0 } ? $"--host --data \"{data}\"" : "--host";
        var xml = Path.Combine(Path.GetTempPath(), $"llamatree-host-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, TaskXml(exe, sid, arguments), Encoding.Unicode);
            // Made again each time (/F), so it always starts this LlamaTree, wherever it's installed now.
            return Schtasks("/Create", "/TN", TaskName, "/XML", xml, "/F") && Schtasks("/Run", "/TN", TaskName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(xml); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// The task: run as you, only while you're signed in (with your desktop's own token: no password stored, no
    /// elevation), on demand only (no trigger), with no time limit and at normal priority (a task's default would end
    /// the host after three days, and run it below normal).
    /// </summary>
    internal static string TaskXml(string exe, string sid, string arguments = "--host") => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts LlamaTree's session host on this computer's desktop when another computer connects to it over SSH.</Description>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(sid)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exe)}</Command>
              <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
              <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    private static bool Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardInput.Close();
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (InvalidOperationException) { } return false; }
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>
    /// Starts the host outside the SSH connection's job (which allows that), inheriting none of its handles, so neither
    /// ends with the other.
    /// </summary>
    public static bool OutsideJob()
    {
        const uint breakawayFromJob = 0x01000000, createNoWindow = 0x08000000;
        var exe = Environment.ProcessPath;
        if (exe == null) return false;
        var si = new Native.STARTUPINFOEX();
        si.StartupInfo.cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.STARTUPINFO>();
        // In a job that doesn't allow it (not sshd's), breaking away is refused: then started as usual.
        if (!Native.CreateProcessW(null, $"\"{exe}\" --host", IntPtr.Zero, IntPtr.Zero, false, breakawayFromJob | createNoWindow,
                IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi) &&
            !Native.CreateProcessW(null, $"\"{exe}\" --host", IntPtr.Zero, IntPtr.Zero, false, createNoWindow,
                IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi))
            return false;
        Native.CloseHandle(pi.hThread);
        Native.CloseHandle(pi.hProcess);
        return true;
    }
}
