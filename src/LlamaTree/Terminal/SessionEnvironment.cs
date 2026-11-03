using System.Runtime.InteropServices;
using System.Text;

namespace LlamaTree.Terminal;

/// <summary>
/// Builds the environment for session processes from the user's registry environment (what a fresh
/// Explorer or Windows Terminal launch gets), rather than inheriting this process's environment.
/// That keeps whatever launched the app (e.g. a Claude Code session, which sets NO_COLOR and
/// CLAUDE_CODE_* markers) from leaking into every session, and picks up PATH changes made since startup.
/// On macOS and Linux it's what the user's login shell sets up (see <see cref="FromLoginShell"/>).
/// </summary>
internal static class SessionEnvironment
{
    /// <summary>Variables that only make sense inside the process that set them (fallback path only).</summary>
    private static readonly string[] StripPrefixes = ["CLAUDE_CODE_", "CLAUDECODE"];
    private static readonly string[] StripNames = ["CLAUDE_PID", "NO_COLOR"];

    /// <summary>Set by a shell for itself (and a terminal for its own windows), so not passed on.</summary>
    private static readonly string[] ShellOwnNames = ["_", "PWD", "OLDPWD", "SHLVL", "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "TERM_SESSION_ID", "ITERM_SESSION_ID"];

    public static Dictionary<string, string> Build()
    {
        if (!OperatingSystem.IsWindows()) return BuildUnix();
        // The registry environment is the user's deliberate configuration, so it is used as-is.
        // Only the inherited fallback gets scrubbed of launcher-specific variables.
        var env = FromUserProfile();
        if (env == null)
        {
            env = FromCurrentProcess();
            foreach (var key in env.Keys.Where(ShouldStrip).ToList()) env.Remove(key);
        }
        // Claude Code and other TUIs render 24-bit colour when they see this.
        env["COLORTERM"] = "truecolor";
        return env;
    }

    private static Dictionary<string, string> BuildUnix()
    {
        var env = new Dictionary<string, string>(FromLoginShell() ?? FromCurrentProcess(), StringComparer.Ordinal);
        foreach (var key in env.Keys.Where(k => ShouldStrip(k) || ShellOwnNames.Contains(k)).ToList()) env.Remove(key);
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        // An app started from the Dock or Finder gets no locale, and programs then fall back to ASCII.
        if (!env.Keys.Any(k => k is "LANG" or "LC_ALL" or "LC_CTYPE")) env["LANG"] = "en_US.UTF-8";
        return env;
    }

    private static readonly object ShellLock = new();
    private static Dictionary<string, string>? shellEnv;
    private static DateTime shellReadAt;
    private static Task? shellReading;

    /// <summary>macOS and Linux: starts reading the login shell's environment, so the first session doesn't wait for it.</summary>
    public static void Warm()
    {
        if (!OperatingSystem.IsWindows()) lock (ShellLock) StartReading();
    }

    /// <summary>
    /// The environment the user's login shell sets up ($SHELL -l -i, reading .zprofile, .zshrc, .bash_profile...):
    /// an app started from the Dock, Finder or a desktop menu doesn't get the PATH a terminal has, where claude, codex,
    /// node and git usually are. It's read in the background (shell startup files can be slow, and sessions start on
    /// the window's thread), and again in the background once it's a minute old, so PATH changes made since show up.
    /// Only the very first read is waited for. Null if it can't be read.
    /// </summary>
    private static Dictionary<string, string>? FromLoginShell()
    {
        Task? first;
        lock (ShellLock)
        {
            if (shellReading == null) StartReading();
            else if (shellReading.IsCompleted && DateTime.UtcNow - shellReadAt > TimeSpan.FromMinutes(1)) StartReading();
            if (shellReadAt != default) return shellEnv;   // read before (a failure too): the latest answer, at once
            first = shellReading;
        }
        first?.Wait(TimeSpan.FromSeconds(12));
        lock (ShellLock) return shellEnv;
    }

    private static void StartReading()
    {
        shellReading = Task.Run(() =>
        {
            var read = ReadLoginShell();
            lock (ShellLock)
            {
                if (read != null) shellEnv = read;   // a failed read keeps the last good one
                shellReadAt = DateTime.UtcNow;
            }
        });
    }

    private static Dictionary<string, string>? ReadLoginShell()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s && File.Exists(s) ? s : "/bin/sh";
        const string marker = "__LLAMATREE_ENV__";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(shell)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            // Interactive too: many people set PATH in .zshrc / .bashrc. Anything the startup files print comes before
            // the marker and is skipped.
            foreach (var a in new[] { "-l", "-i", "-c", $"printf '{marker}'; command env -0" }) psi.ArgumentList.Add(a);
            foreach (var name in StripNames) psi.Environment.Remove(name);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            p.StandardInput.Close();
            _ = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!output.Wait(TimeSpan.FromSeconds(10)))
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return null;
            }
            var text = output.Result;
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in text[(start + marker.Length)..].Split('\0'))
            {
                var eq = entry.IndexOf('=');
                if (eq > 0) env[entry[..eq]] = entry[(eq + 1)..];
            }
            return env.ContainsKey("PATH") ? env : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool ShouldStrip(string name) =>
        StripNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        StripPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Environment block layout for CreateProcess: sorted "NAME=value\0" entries, then a final "\0".</summary>
    public static string ToBlock(IReadOnlyDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(key).Append('=').Append(value).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    private static Dictionary<string, string>? FromUserProfile()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, out var token)) return null;
        try
        {
            if (!CreateEnvironmentBlock(out var block, token, false)) return null;
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var ptr = block;
                while (true)
                {
                    var entry = Marshal.PtrToStringUni(ptr);
                    if (string.IsNullOrEmpty(entry)) break;
                    // Entries like "=C:=C:\" (per-drive cwd) start with '=', so split at the first '=' after position 0.
                    var eq = entry.IndexOf('=', 1);
                    if (eq > 0) env[entry[..eq]] = entry[(eq + 1)..];
                    ptr += (entry.Length + 1) * sizeof(char);
                }
                return env.Count > 0 ? env : null;
            }
            finally
            {
                DestroyEnvironmentBlock(block);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static Dictionary<string, string> FromCurrentProcess()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        return env;
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_DUPLICATE = 0x0002;

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
