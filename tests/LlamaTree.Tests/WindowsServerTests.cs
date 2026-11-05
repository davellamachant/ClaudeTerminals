using System.Diagnostics;
using System.Xml.Linq;
using LlamaTree.Hosting;

namespace LlamaTree.Tests;

/// <summary>A Windows computer as a server: telling one from what ssh said, and what's run there.</summary>
public sealed class WindowsServerTests
{
    [Theory]
    [InlineData(9009, "'sh' is not recognized as an internal or external command,\r\noperable program or batch file.", true)]
    [InlineData(9009, "Der Befehl \"sh\" ist entweder falsch geschrieben oder\r\nkonnte nicht gefunden werden.", true)]
    [InlineData(49, "'sh' is not recognized as an internal or external command,", true)]   // 9009's low byte, from a Unix ssh
    [InlineData(126, Ssh.WindowsMessage, true)]   // Git's sh, on Windows
    [InlineData(127, Ssh.NotInstalledMessage, false)]
    [InlineData(255, "ssh: connect to host build-box port 22: Connection refused", false)]
    [InlineData(null, "Permission denied (publickey).", false)]
    public void A_Windows_server_shows_by_its_shell_having_no_sh(int? code, string errors, bool windows) =>
        Assert.Equal(windows, Ssh.SaysWindows(code, errors));

    /// <summary>Runs the bridge command for Windows as Windows' OpenSSH Server does (cmd /c, with pipes), in a made-up profile folder.</summary>
    private static (int Code, string Output, string Errors) RunBridgeCommand(string localAppData, string input)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = "/c " + Ssh.WindowsRemoteCommand,
        };
        psi.Environment["LOCALAPPDATA"] = localAppData;
        psi.Environment["ProgramFiles"] = localAppData;   // nothing for everyone either
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(input);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(15000));
        return (p.ExitCode, output.Result, errors.Result);
    }

    [Fact]
    public void Without_LlamaTree_the_bridge_command_for_Windows_says_so()
    {
        if (!OperatingSystem.IsWindows()) return;
        var empty = Directory.CreateTempSubdirectory("lt-win-").FullName;
        try
        {
            var (code, _, errors) = RunBridgeCommand(empty, "");
            Assert.Equal(Ssh.NotInstalled, code);
            Assert.Contains(Ssh.NotInstalledMessage, errors);
        }
        finally { Directory.Delete(empty, true); }
    }

    [Theory]
    [InlineData("LlamaTree")]
    [InlineData("Claude Terminals")]   // installed before the rename, and updated since: setup keeps the folder
    public void The_bridge_command_for_Windows_runs_LlamaTree_attach_on_the_connection(string installedIn)
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Directory.CreateTempSubdirectory("lt-win-").FullName;
        try
        {
            // A stand-in for LlamaTree.exe: a copy of cmd, which runs what comes down the connection (and ignores --attach).
            var app = Path.Combine(folder, "Programs", installedIn);
            Directory.CreateDirectory(app);
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(app, "LlamaTree.exe"));
            var (code, output, _) = RunBridgeCommand(folder, "echo through the pipe\r\nexit 7\r\n");
            Assert.Equal(7, code);   // its exit code is the bridge command's
            Assert.Contains("\r\nthrough the pipe\r\n", output);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void The_host_lock_across_logon_sessions_is_held_once()
    {
        var name = "LlamaTree.Test." + Guid.NewGuid().ToString("N");
        using var first = InstanceLock.TakeAcrossSessions(name, out var wasFirst);
        Assert.True(wasFirst);
        if (!OperatingSystem.IsWindows()) { Assert.Null(first); return; }   // there, Take already is
        var again = Task.Run(() => { using var m = InstanceLock.TakeAcrossSessions(name, out var f); return f; }).Result;
        Assert.False(again);
    }

    [Fact]
    public void Installing_on_Windows_runs_the_release_install_ps1_in_Windows_PowerShell()
    {
        var command = ServerInstall.WindowsCommand("https://github.com/LlamachantTechnology/LlamaTree/releases/latest/download/install.sh", null);
        Assert.StartsWith("powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"", command);
        Assert.Contains("irm 'https://github.com/LlamachantTechnology/LlamaTree/releases/latest/download/install.ps1' -UseBasicParsing | iex\"", command);
        Assert.DoesNotContain("LLAMATREE_UPDATE_URL", command);
        Assert.Contains("$env:LLAMATREE_UPDATE_URL='http://127.0.0.1:5000/latest'; ",
            ServerInstall.WindowsCommand("http://127.0.0.1:5000/install.sh", "http://127.0.0.1:5000/latest"));
    }

    [Fact]
    public void The_host_task_runs_LlamaTree_as_you_on_your_desktop_with_no_time_limit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var xml = XDocument.Parse(WindowsHostStart.TaskXml(@"C:\Users\A & B\Programs\LlamaTree\LlamaTree.exe", "S-1-5-21-1-2-3-1001"));
        XNamespace t = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var principal = xml.Root!.Element(t + "Principals")!.Element(t + "Principal")!;
        Assert.Equal("S-1-5-21-1-2-3-1001", principal.Element(t + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(t + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(t + "RunLevel")!.Value);
        var settings = xml.Root.Element(t + "Settings")!;
        Assert.Equal("PT0S", settings.Element(t + "ExecutionTimeLimit")!.Value);
        Assert.Equal("false", settings.Element(t + "StopIfGoingOnBatteries")!.Value);
        var exec = xml.Root.Element(t + "Actions")!.Element(t + "Exec")!;
        Assert.Equal(@"C:\Users\A & B\Programs\LlamaTree\LlamaTree.exe", exec.Element(t + "Command")!.Value);
        Assert.Equal("--host", exec.Element(t + "Arguments")!.Value);
        Assert.Null(xml.Root.Element(t + "Triggers"));   // on demand only
    }
}
