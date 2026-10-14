namespace ClaudeTerminals.Terminal;

/// <summary>
/// Turns a user command such as "claude --continue" into a CreateProcess command line.
/// Real executables are launched directly; anything else (.cmd shims, shell built-ins) goes through cmd.exe.
/// </summary>
internal static class CommandLineResolver
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC";

    /// <param name="env">The session's environment; its PATH, PATHEXT and ComSpec are used for the lookup.</param>
    public static string Resolve(string command, IReadOnlyDictionary<string, string> env)
    {
        command = command.Trim();
        if (command.Length == 0) command = "claude";

        var (program, rest) = SplitFirstToken(command);
        var found = FindProgram(program, env.GetValueOrDefault("PATH") ?? "", env.GetValueOrDefault("PATHEXT") ?? DefaultPathExt);
        if (found != null && IsExe(found))
            return Quote(found) + rest;

        // Anything else (npm's claude.cmd, a shell built-in) runs through cmd. A program found on PATH is handed over by
        // its full path: given a bare name, cmd would search the session folder - the repository - first and could run a
        // claude.bat that happens to be there. For the same reason a bare name that is neither on PATH nor a cmd
        // built-in is not handed to cmd at all. /s makes cmd strip only the outer quotes added here, so quoted paths
        // and arguments inside the command survive.
        if (found == null && IsBareName(program) && !CmdBuiltIns.Contains(program))
            throw new FileNotFoundException($"'{program}' was not found on PATH.");
        // Quotes don't stop cmd expanding %NAME% (or !NAME! with delayed expansion, turned off below), so a path containing
        // them could launch something other than the file that was checked.
        if (found != null && found.Contains('%'))
            throw new InvalidOperationException($"Can't run '{found}' safely: cmd would expand the % in its path.");
        var comSpec = env.GetValueOrDefault("ComSpec") ?? "cmd.exe";
        var line = found != null ? $"\"{found}\"{rest}" : command;
        return $"{Quote(comSpec)} /d /v:off /s /c \"{line}\"";
    }

    private static readonly HashSet<string> CmdBuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "assoc", "break", "call", "cd", "chdir", "cls", "color", "copy", "date", "del", "dir", "echo", "endlocal", "erase",
        "exit", "for", "ftype", "goto", "if", "md", "mkdir", "mklink", "move", "path", "pause", "popd", "prompt", "pushd",
        "rd", "rem", "ren", "rename", "rmdir", "set", "setlocal", "shift", "start", "time", "title", "type", "ver", "verify", "vol",
    };

    private static bool IsBareName(string program) =>
        !Path.IsPathRooted(program) && !program.Contains('\\') && !program.Contains('/');

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

    private static bool IsExe(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds a program the way cmd does (each PATH folder in turn, trying each PATHEXT extension), except that the current
    /// folder is never searched. Null for a relative path (left to cmd, in the session folder) or anything not found.
    /// </summary>
    internal static string? FindProgram(string program, string path, string pathExt)
    {
        var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // The name as typed counts only if its extension is a program type ("claude.cmd"); otherwise each PATHEXT
        // extension is tried in order, so "tool.v2" can find tool.v2.cmd.
        var exact = extensions.Contains(Path.GetExtension(program), StringComparer.OrdinalIgnoreCase) ? new[] { program } : [];
        var candidates = exact.Concat(extensions.Select(e => program + e)).ToArray();

        // Only fully qualified locations: a drive-relative one such as C:tools depends on a current directory, which may
        // be a different folder here than in the session.
        if (Path.IsPathFullyQualified(program)) return candidates.FirstOrDefault(File.Exists);
        if (Path.IsPathRooted(program) || program.Contains('\\') || program.Contains('/')) return null;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(dir)) continue;
            foreach (var candidate in candidates)
            {
                try
                {
                    var full = Path.Combine(dir, candidate);
                    if (File.Exists(full)) return full;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
