using System.Diagnostics;
using System.Text;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Sessions;

/// <summary>
/// Runs an agent once, headless, with a prompt on its standard input and its answer on standard output
/// (<see cref="AgentProfile.HeadlessCommand"/>: claude -p with no tools, or codex exec read-only). It runs with the
/// user's own sign-in, in the same clean environment sessions get, and saves no conversation. The commit message
/// suggester and the plugins' ask request use it.
/// </summary>
internal static class HeadlessAgent
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>The agent that answers for a session: its own, or Claude for one with no headless command (or no agent).</summary>
    public static AgentProfile Writer(AgentProfile? agent) => agent?.HeadlessCommand != null ? agent : Agents.Claude;

    /// <param name="dir">The folder to run in: one the user already trusts, such as a session's.</param>
    /// <param name="task">What the agent was asked to do, for the error when it can't: "suggest a message", "answer".</param>
    public static async Task<string> RunAsync(AgentProfile agent, string dir, string prompt, string task, CancellationToken ct)
    {
        var env = SessionEnvironment.Build();
        string line;
        try
        {
            line = CommandLineResolver.Resolve(agent.HeadlessCommand!, env);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException($"{agent.Name} ({agent.Programs[0]}) wasn't found on PATH, so it can't {task}.");
        }
        var (program, arguments) = SplitProgram(line);
        var psi = new ProcessStartInfo(program, arguments)
        {
            WorkingDirectory = Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // The same clean environment sessions get, not this app's (which may have been started from a Claude session).
        psi.Environment.Clear();
        foreach (var (name, value) in env) psi.Environment[name] = value;

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(Timeout);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {agent.Name}.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timer.Token);
            var stderr = process.StandardError.ReadToEndAsync(timer.Token);
            await process.StandardInput.WriteAsync(prompt.AsMemory(), timer.Token);
            process.StandardInput.Close();
            var output = await stdout;
            var error = await stderr;
            await process.WaitForExitAsync(timer.Token);
            if (process.ExitCode != 0)
            {
                var why = (error.Trim().Length > 0 ? error : output).Trim();
                throw new InvalidOperationException($"{agent.Name} couldn't {task}: " + (why.Length > 400 ? why[..400] + "…" : why));
            }
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{agent.Name} didn't answer within {Timeout.TotalMinutes:0} minutes.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    /// <summary>CommandLineResolver gives a whole command line; the program is its first (possibly quoted) token.</summary>
    internal static (string Program, string Arguments) SplitProgram(string line)
    {
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            return (line[1..end], line[(end + 1)..].TrimStart());
        }
        var space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }
}
