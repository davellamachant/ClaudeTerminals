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
    /// <summary>What's run on the server: LlamaTree's bridge to its session host. LLAMATREE_REMOTE_COMMAND overrides it.</summary>
    private static string RemoteCommand =>
        Environment.GetEnvironmentVariable("LLAMATREE_REMOTE_COMMAND") is { Length: > 0 } c ? c : "llamatree --attach";

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
