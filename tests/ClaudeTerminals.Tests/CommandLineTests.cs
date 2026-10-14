using System.Collections;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Tests;

/// <summary>Review finding F12: how a session command is turned into a process, run for real in a pseudo console.</summary>
public sealed class CommandLineTests : IDisposable
{
    private readonly string top = Path.Combine(Path.GetTempPath(), "ct-cmd-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string onPath;    // stands in for e.g. %APPDATA%\npm
    private readonly string session;   // the repository the session runs in

    public CommandLineTests()
    {
        onPath = Path.Combine(top, "on path");
        session = Path.Combine(top, "repo");
        Directory.CreateDirectory(onPath);
        Directory.CreateDirectory(session);
    }

    public void Dispose()
    {
        try { Directory.Delete(top, true); } catch (IOException) { }
    }

    private Dictionary<string, string> Env(params string[] path)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string?)e.Value ?? "";
        env["PATH"] = string.Join(';', path.Append(Path.Combine(Environment.SystemDirectory)));
        return env;
    }

    /// <summary>A script that records that it ran (and its arguments) in a marker file next to it.</summary>
    /// <remarks>Arguments go through delayed expansion so characters such as &amp; in them can't break the script itself.</remarks>
    private static void Script(string path, string marker) =>
        File.WriteAllText(path, string.Join("\r\n",
            "@echo off", "chcp 65001 >nul", "setlocal EnableDelayedExpansion",
            "set \"a1=%~1\"", "set \"a2=%~2\"", "set \"a3=%~3\"",
            $">\"%~dp0{marker}.ran\" echo {marker}^|!a1!^|!a2!^|!a3!", ""));

    /// <summary>Runs a resolved command line in a pseudo console with the session folder as current directory.</summary>
    private async Task<int> Run(string commandLine, Dictionary<string, string> env)
    {
        using var p = new PseudoConsoleProcess();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Exited += code => exited.TrySetResult(code);
        p.Start(commandLine, session, SessionEnvironment.ToBlock(env), 120, 30);
        return await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_shim_on_PATH_runs_and_same_named_scripts_in_the_repository_do_not()
    {
        Script(Path.Combine(onPath, "claude.cmd"), "real");
        foreach (var decoy in new[] { "claude.bat", "claude.cmd", "claude.com", "claude.exe" })
            File.WriteAllText(Path.Combine(session, decoy), "@echo decoy> \"%~dp0decoy.ran\"\r\n");
        File.WriteAllText(Path.Combine(session, "claude.vbs"), "CreateObject(\"Scripting.FileSystemObject\").CreateTextFile(\"decoy.ran\")");
        File.WriteAllText(Path.Combine(session, "claude.js"), "new ActiveXObject(\"Scripting.FileSystemObject\").CreateTextFile(\"decoy.ran\")");
        var env = Env(onPath);

        var line = CommandLineResolver.Resolve("claude --flag", env);
        await Run(line, env);

        Assert.Contains(Path.Combine(onPath, "claude.cmd"), line, StringComparison.OrdinalIgnoreCase);   // the extension comes back in PATHEXT case
        Assert.StartsWith("real|--flag", File.ReadAllText(Path.Combine(onPath, "real.ran")));
        Assert.False(File.Exists(Path.Combine(session, "decoy.ran")));
    }

    [Fact]
    public void A_name_found_nowhere_on_PATH_is_refused_rather_than_searched_for_in_the_repository()
    {
        File.WriteAllText(Path.Combine(session, "claude.bat"), "@echo decoy> \"%~dp0decoy.ran\"\r\n");

        Assert.Throws<FileNotFoundException>(() => CommandLineResolver.Resolve("claude", Env(onPath)));
        Assert.False(File.Exists(Path.Combine(session, "decoy.ran")));
    }

    [Fact]
    public async Task Quoted_script_paths_and_arguments_arrive_intact()
    {
        var script = Path.Combine(onPath, "args.cmd");
        Script(script, "args");
        var env = Env();

        await Run(CommandLineResolver.Resolve($"\"{script}\" \"two words\" \"\" \"ünï & co\"", env), env);

        Assert.Equal("args|two words||ünï & co", File.ReadAllText(Path.Combine(onPath, "args.ran")).TrimEnd());
    }

    [Fact]
    public async Task Quoted_executable_paths_with_spaces_and_quoted_arguments_arrive_intact()
    {
        var git = CommandLineResolver.FindProgram("git", Environment.GetEnvironmentVariable("PATH") ?? "", ".EXE");
        Assert.NotNull(git);
        var target = Path.Combine(top, "new repo ü");
        var env = Env(Path.GetDirectoryName(git)!);

        var line = CommandLineResolver.Resolve($"\"{git}\" init -q \"{target}\"", env);
        await Run(line, env);

        Assert.StartsWith($"\"{git}\" ", line);   // a real .exe is started directly, not through cmd
        Assert.True(Directory.Exists(Path.Combine(target, ".git")));
    }

    [Fact]
    public void Drive_relative_PATH_entries_are_not_searched()
    {
        // "C:tools" means "tools under whatever the current folder on C: is", which can differ between this app and the session.
        var relative = "ct-relpath-" + Guid.NewGuid().ToString("N")[..8];
        var underCwd = Path.Combine(Environment.CurrentDirectory, relative);
        Directory.CreateDirectory(underCwd);
        try
        {
            File.WriteAllText(Path.Combine(underCwd, "claude.cmd"), "@echo decoy\r\n");
            Script(Path.Combine(onPath, "claude.cmd"), "real");
            var drive = Path.GetPathRoot(Environment.CurrentDirectory)![..2];   // e.g. "C:"

            var found = CommandLineResolver.FindProgram("claude", $"{drive}{relative};{onPath}", ".COM;.EXE;.BAT;.CMD");

            Assert.Equal(Path.Combine(onPath, "claude.CMD"), found, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(underCwd, true);
        }
    }

    [Fact]
    public void A_found_script_whose_path_cmd_would_expand_is_refused()
    {
        var odd = Path.Combine(top, "tools %USERNAME% dir");
        Directory.CreateDirectory(odd);
        Script(Path.Combine(odd, "claude.cmd"), "real");

        Assert.Throws<InvalidOperationException>(() => CommandLineResolver.Resolve("claude", Env(odd)));
    }

    [Fact]
    public void A_dotted_name_still_gets_PATHEXT_extensions()
    {
        File.WriteAllText(Path.Combine(onPath, "tool.v2.cmd"), "@echo ok\r\n");

        Assert.Equal(Path.Combine(onPath, "tool.v2.CMD"), CommandLineResolver.FindProgram("tool.v2", onPath, ".COM;.EXE;.BAT;.CMD"), ignoreCase: true);
    }

    [Fact]
    public void A_blank_PATHEXT_falls_back_to_the_default_extensions()
    {
        File.WriteAllText(Path.Combine(onPath, "claude.exe"), "");
        var env = Env(onPath);
        env["PATHEXT"] = "  ";

        Assert.Contains(Path.Combine(onPath, "claude.exe"), CommandLineResolver.Resolve("claude", env), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Not_found_names_the_PATH_entries_that_were_skipped()
    {
        var env = Env(onPath, @"%USERPROFILE%\.local\bin", @"tools\bin");

        var error = Assert.Throws<FileNotFoundException>(() => CommandLineResolver.Resolve("no-such-tool", env));

        Assert.Contains("'no-such-tool' was not found on PATH", error.Message);
        Assert.Contains(@"%USERPROFILE%\.local\bin", error.Message);
        Assert.Contains(@"tools\bin", error.Message);
        Assert.DoesNotContain(onPath, error.Message);   // fully qualified entries were searched, so aren't listed
    }

    [Fact]
    public void Not_found_with_a_clean_PATH_keeps_the_short_message() =>
        Assert.Equal("'no-such-tool' was not found on PATH.",
            Assert.Throws<FileNotFoundException>(() => CommandLineResolver.Resolve("no-such-tool", Env(onPath))).Message);

    [Fact]
    public void Cmd_built_ins_still_run_through_cmd() =>
        Assert.Contains("/d /v:off /s /c \"echo hi\"", CommandLineResolver.Resolve("echo hi", Env(onPath)));
}
