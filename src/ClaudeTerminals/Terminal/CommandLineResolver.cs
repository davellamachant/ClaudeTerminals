namespace ClaudeTerminals.Terminal;

/// <summary>
/// Turns a user command such as "claude --continue" into a CreateProcess command line.
/// Real executables are launched directly; anything else (.cmd shims, shell built-ins) goes through cmd.exe.
/// </summary>
internal static class CommandLineResolver
{
    /// <param name="env">The session's environment; its PATH and ComSpec are used for the lookup.</param>
    public static string Resolve(string command, IReadOnlyDictionary<string, string> env)
    {
        command = command.Trim();
        if (command.Length == 0) command = "claude";

        var (program, rest) = SplitFirstToken(command);
        var exe = FindExecutable(program, env.GetValueOrDefault("PATH") ?? "");
        if (exe != null)
            return Quote(exe) + rest;

        var comSpec = env.GetValueOrDefault("ComSpec") ?? "cmd.exe";
        return $"{Quote(comSpec)} /d /c {command}";
    }

    private static (string Program, string Arguments) SplitFirstToken(string command)
    {
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..]);
        }
        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[space..]);
    }

    private static string? FindExecutable(string program, string path)
    {
        static bool IsExe(string path) =>
            File.Exists(path) && (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".com", StringComparison.OrdinalIgnoreCase));

        var candidates = Path.HasExtension(program) ? [program] : new[] { program + ".exe", program + ".com" };

        if (Path.IsPathRooted(program) || program.Contains('\\') || program.Contains('/'))
            return candidates.FirstOrDefault(IsExe);

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), candidate);
                    if (IsExe(full)) return full;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
