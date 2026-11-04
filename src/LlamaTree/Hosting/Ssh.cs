using System.Diagnostics;
using System.Text;
using LlamaTree.Hosting.Link;

namespace LlamaTree.Hosting;

/// <summary>
/// ssh to a server's LlamaTree bridge (see <see cref="HostBridge"/>): its output and input are the connection to the
/// session host there. Used by a window showing a server's sessions (see <see cref="HostLink"/>) and by this
/// computer's host for sessions it shows from servers (see <see cref="ServerConnection"/>).
/// </summary>
internal sealed class Ssh
{
    /// <summary>
    /// What's run on the server: LlamaTree's bridge to its session host. The llamatree command where the server's shell
    /// finds it (a package's /usr/bin/llamatree), else where install.sh puts a copy of your own (~/.local/bin, which a
    /// shell started by ssh often doesn't have on its PATH). In sh, whatever your login shell is (fish, csh…).
    /// LLAMATREE_REMOTE_COMMAND overrides it.
    /// </summary>
    private static string RemoteCommand =>
        Environment.GetEnvironmentVariable("LLAMATREE_REMOTE_COMMAND") is { Length: > 0 } c ? c : DefaultRemoteCommand;

    /// <summary>Exit code of the bridge command when LlamaTree isn't installed on the server.</summary>
    public const int NotInstalled = 127;

    /// <summary>What the bridge command says when LlamaTree isn't installed on the server.</summary>
    public const string NotInstalledMessage = "LlamaTree: it is not installed on this server.";

    internal const string DefaultRemoteCommand =
        "sh -c 'if command -v llamatree >/dev/null 2>&1; then exec llamatree --attach; " +
        "elif [ -x \"$HOME/.local/bin/llamatree\" ]; then exec \"$HOME/.local/bin/llamatree\" --attach; " +
        "else echo \"LlamaTree: it is not installed on this server.\" >&2; exit 127; fi'";

    private readonly StringBuilder errors = new();
    private readonly TaskCompletionSource errorsDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The connection, or null if ssh couldn't be started (see <see cref="Failure"/>).</summary>
    public Stream? Stream { get; private init; }

    /// <summary>Why ssh couldn't be started, when it couldn't.</summary>
    public string? Failure { get; private init; }

    /// <summary>Whether <paramref name="target"/> can be handed to ssh as where to connect: never an option, a space or a control character.</summary>
    public static bool IsValidTarget(string? target) =>
        target is { Length: > 0 and <= 255 } && target[0] != '-' && !target.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>
    /// What ssh said on its error output, once it has ended (or after <paramref name="wait"/>, if it hasn't): why the
    /// connection failed or ended.
    /// </summary>
    public async Task<string> ErrorsAsync(TimeSpan wait)
    {
        await Task.WhenAny(errorsDone.Task, Task.Delay(wait));
        lock (errors) return errors.ToString().Trim();
    }

    /// <summary>
    /// Runs a shell script on <paramref name="target"/> (sh reading it from its input, so nothing of it is on a command
    /// line): its exit code, and what it printed (its output and errors, the last 8000 characters). Ended after
    /// <paramref name="timeout"/>.
    /// </summary>
    public static async Task<(int Code, string Output)> RunScriptAsync(string target, string script, TimeSpan timeout)
    {
        var program = Environment.GetEnvironmentVariable("LLAMATREE_SSH") is { Length: > 0 } p ? p : "ssh";
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "--", target, "sh -s" }) psi.ArgumentList.Add(a);
        Process ssh;
        try { ssh = Process.Start(psi) ?? throw new InvalidOperationException("ssh didn't start."); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("ssh isn't installed on this computer (or isn't on the PATH)."); }
        using (ssh)
        {
            var output = new StringBuilder();
            void Keep(string? line)
            {
                if (line == null) return;
                lock (output)
                {
                    output.AppendLine(line);
                    if (output.Length > 8000) output.Remove(0, output.Length - 8000);
                }
            }
            ssh.OutputDataReceived += (_, e) => Keep(e.Data);
            ssh.ErrorDataReceived += (_, e) => Keep(e.Data);
            ssh.BeginOutputReadLine();
            ssh.BeginErrorReadLine();
            await ssh.StandardInput.WriteAsync(script.Replace("\r\n", "\n"));
            ssh.StandardInput.Close();
            using var cancel = new CancellationTokenSource(timeout);
            try { await ssh.WaitForExitAsync(cancel.Token); }
            catch (OperationCanceledException)
            {
                try { ssh.Kill(true); } catch (Exception) { }
                throw new InvalidOperationException($"It took longer than {timeout.TotalMinutes:0} minutes, so it was stopped.");
            }
            ssh.WaitForExit();   // the output read to its end
            lock (output) return (ssh.ExitCode, output.ToString().Trim());
        }
    }

    /// <summary>Starts ssh to <paramref name="target"/>. No password prompts (there's nowhere to type one): a key, or ssh-agent.</summary>
    public static Ssh Start(string target)
    {
        // LLAMATREE_SSH: another ssh program, for one that isn't on the PATH.
        var program = Environment.GetEnvironmentVariable("LLAMATREE_SSH") is { Length: > 0 } p ? p : "ssh";
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Keep-alives notice a dead connection within about 45 seconds.
        foreach (var a in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "--", target, RemoteCommand })
            psi.ArgumentList.Add(a);
        Process? ssh;
        try
        {
            ssh = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new Ssh { Failure = "ssh isn't installed on this computer (or isn't on the PATH). LlamaTree uses it to reach the server." };
        }
        if (ssh == null) return new Ssh { Failure = "ssh didn't start." };
        var result = new Ssh
        {
            Stream = new DuplexStream(ssh.StandardOutput.BaseStream, ssh.StandardInput.BaseStream, () =>
            {
                try { if (!ssh.HasExited) ssh.Kill(); } catch (Exception) { }
                ssh.Dispose();
            }),
        };
        ssh.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                result.errorsDone.TrySetResult();   // ssh has ended
                return;
            }
            lock (result.errors) { if (result.errors.Length < 4000) result.errors.AppendLine(e.Data); }
        };
        ssh.BeginErrorReadLine();
        return result;
    }
}
