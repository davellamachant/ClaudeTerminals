using LlamaTree.Platform;
using LlamaTree.Sessions;
using LlamaTree.Terminal;

namespace LlamaTree.Tests;

/// <summary>macOS and Linux: commands run through /bin/sh in a Unix pseudo terminal, and what the app adds is quoted for the shell.</summary>
public sealed class UnixCommandLineTests : IDisposable
{
    private readonly string top = Path.Combine(Path.GetTempPath(), "lt-unix-" + Guid.NewGuid().ToString("N")[..8]);

    public UnixCommandLineTests() => Directory.CreateDirectory(top);

    public void Dispose()
    {
        try { Directory.Delete(top, true); } catch (IOException) { }
    }

    [UnixFact]
    public void Arguments_are_single_quoted_so_the_shell_expands_nothing()
    {
        Assert.Equal("'plain'", CommandLineResolver.QuoteArgument("plain"));
        Assert.Equal("' --help'", CommandLineResolver.QuoteArgument("--help"));   // never read as an option
        Assert.Equal("'$HOME `id` $(id) \"x\" \\ !'", CommandLineResolver.QuoteArgument("$HOME `id` $(id) \"x\" \\ !"));
        Assert.Equal("'it'\\''s'", CommandLineResolver.QuoteArgument("it's"));
        Assert.Equal("'/Users/me/Library/Application Support/LlamaTree/m.json'",
            CommandLineResolver.QuotePath("/Users/me/Library/Application Support/LlamaTree/m.json"));
    }

    [UnixFact]
    public void Claude_gets_its_theme_panels_and_first_prompt_quoted_for_the_shell()
    {
        Assert.Equal("claude --continue --settings 'T.json'", ClaudeAgent.PrepareCommand("claude --continue", null, () => true, "T.json"));
        var line = ClaudeAgent.PrepareCommand("claude --model opus", null, () => true, "T.json", "M.json",
            newConversation: true, firstPrompt: "Work on \"LT-475\"");
        Assert.Equal("claude 'Work on \"LT-475\"' --model opus --settings 'T.json' --mcp-config 'M.json' --allowedTools mcp__llamatree", line);
        Assert.Equal("codex 'fix it' -m o3", CodexAgent.PrepareCommand("codex -m o3", null, _ => true, newConversation: true, firstPrompt: "fix it"));
    }

    [UnixFact]
    public void A_program_that_isnt_on_PATH_is_reported_but_shell_syntax_is_left_to_the_shell()
    {
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
        var e = Assert.Throws<FileNotFoundException>(() => CommandLineResolver.ResolveArgv("no-such-program-here --x", env));
        Assert.Contains("no-such-program-here", e.Message);
        Assert.Equal(["/bin/sh", "-c", "sh -c true"], CommandLineResolver.ResolveArgv("sh -c true", env));
        Assert.Equal(["/bin/sh", "-c", "A=1 env"], CommandLineResolver.ResolveArgv("A=1 env", env));
        Assert.Equal(["/bin/sh", "-c", "trap '' INT; true"], CommandLineResolver.ResolveArgv("trap '' INT; true", env));
        Assert.True(CommandLineResolver.RunsDirectly("sh", env));
        Assert.False(CommandLineResolver.RunsDirectly("no-such-program-here", env));
    }

    /// <summary>A prompt full of shell syntax arrives as one argument, exactly, and runs nothing.</summary>
    [UnixFact]
    public async Task A_quoted_prompt_reaches_the_program_exactly()
    {
        var marker = Path.Combine(top, "ran");
        var prompt = $"$(touch {marker}) `touch {marker}` ; touch {marker} 'it's' \"q\" $HOME \\n";
        var (output, code) = await Run("printf '[%s]\\n' " + CommandLineResolver.QuoteArgument(prompt), top);
        Assert.Equal(0, code);
        Assert.Contains("[" + prompt + "]", output.Replace("\r\n", "\n"));
        Assert.False(File.Exists(marker));
    }

    [UnixFact]
    public async Task The_program_runs_in_the_session_folder_in_a_terminal_of_its_own()
    {
        var (output, code) = await Run("pwd; tty; stty size; sid=$( (ps -o sid= -p $$ 2>/dev/null || cut -d' ' -f6 /proc/$$/stat) | tr -d ' '); [ \"$sid\" = \"$$\" ] && echo own-session; exit 4", top);
        Assert.Equal(4, code);
        Assert.Contains(PathText.FinalPath(top)!, output);
        Assert.Contains("/dev/", output);
        Assert.Contains("30 100", output);
        Assert.Contains("own-session", output);
    }

    [UnixFact]
    public async Task Resizing_tells_the_program()
    {
        using var p = PtyProcess.Create();
        var output = new System.Text.StringBuilder();
        var ready = new TaskCompletionSource();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Output += t => { lock (output) { output.Append(t); if (output.ToString().Contains("ready")) ready.TrySetResult(); } };
        p.Exited += c => exited.TrySetResult(c);
        p.Start("trap 'stty size; exit 0' WINCH; echo ready; sleep 10 & wait", top, SessionEnvironment.Build(), 100, 30);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        p.Resize(120, 40);
        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        lock (output) Assert.Contains("40 120", output.ToString());
    }

    [UnixFact]
    public async Task Disposing_hangs_up_and_kills_the_whole_tree()
    {
        var p = PtyProcess.Create();
        p.Start("sleep 60 & sleep 60; wait", top, SessionEnvironment.Build(), 80, 24);
        for (var i = 0; i < 50 && p.ProcessIds().Length < 3; i++) await Task.Delay(100);
        var tree = p.ProcessIds();
        Assert.True(tree.Length >= 3, $"expected the shell and two sleeps, got {tree.Length}");
        p.Dispose();
        for (var i = 0; i < 40 && tree.Any(Alive); i++) await Task.Delay(100);
        Assert.DoesNotContain(tree, Alive);
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && !(OperatingSystem.IsLinux() && File.ReadAllText($"/proc/{pid}/stat").Contains(") Z "));
        }
        catch (Exception) { return false; }
    }

    private static async Task<(string Output, int Code)> Run(string command, string dir)
    {
        using var p = PtyProcess.Create();
        var output = new System.Text.StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Output += t => { lock (output) output.Append(t); };
        p.Exited += c => exited.TrySetResult(c);
        p.Start(command, dir, SessionEnvironment.Build(), 100, 30);
        var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
        lock (output) return (output.ToString(), code);
    }
}
