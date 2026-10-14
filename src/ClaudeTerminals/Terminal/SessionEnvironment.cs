using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeTerminals.Terminal;

/// <summary>
/// Builds the environment for session processes from the user's registry environment (what a fresh
/// Explorer or Windows Terminal launch gets), rather than inheriting this process's environment.
/// That keeps whatever launched the app (e.g. a Claude Code session, which sets NO_COLOR and
/// CLAUDE_CODE_* markers) from leaking into every session, and picks up PATH changes made since startup.
/// </summary>
internal static class SessionEnvironment
{
    /// <summary>Variables that only make sense inside the process that set them (fallback path only).</summary>
    private static readonly string[] StripPrefixes = ["CLAUDE_CODE_", "CLAUDECODE"];
    private static readonly string[] StripNames = ["CLAUDE_PID", "NO_COLOR"];

    public static Dictionary<string, string> Build()
    {
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

    private static bool ShouldStrip(string name) =>
        StripNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        StripPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Environment block layout for CreateProcess: sorted "NAME=value\0" entries, then a final "\0".</summary>
    public static string ToBlock(Dictionary<string, string> env)
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
