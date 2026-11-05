namespace LlamaTree.Hosting;

/// <summary>
/// Installs LlamaTree on a server that doesn't have it, with the release's own install script (see tools/install).
/// On Linux, install.sh: run over ssh with no terminal, it installs the package with sudo where sudo needs no password,
/// and otherwise a copy of your own in ~/.local/share/LlamaTree (which updates itself). On Windows, install.ps1: setup,
/// for this user only, with no admin. Both check what they download against GitHub's SHA-256.
/// It's the latest release, which may be newer than this computer's LlamaTree; a server that ends up older says so
/// on its badge, where it can be updated.
/// </summary>
internal static class ServerInstall
{
    private const string ScriptUrl = "https://github.com/LlamachantTechnology/LlamaTree/releases/latest/download/install.sh";

    /// <summary>Installs it on <paramref name="target"/>. Returns what to tell you; throws with why it didn't.</summary>
    public static async Task<string> InstallAsync(string target)
    {
        // The scripts read LLAMATREE_UPDATE_URL themselves: a test server standing in for GitHub stands in for it there too.
        var url = Environment.GetEnvironmentVariable("LLAMATREE_INSTALL_SCRIPT_URL") is { Length: > 0 } test ? test : ScriptUrl;
        var api = Environment.GetEnvironmentVariable("LLAMATREE_UPDATE_URL");
        // Each sits in quotes on the server: none may hold one (or anything cmd would act on, for Windows).
        if (url.IndexOfAny(['\'', '"', '%', '^', '&', '|', '<', '>']) >= 0) throw new InvalidOperationException("The install script's address isn't usable.");
        if (api != null && api.IndexOfAny(['\'', '"', '%', '^', '&', '|', '<', '>']) >= 0) throw new InvalidOperationException("LLAMATREE_UPDATE_URL isn't usable (it holds a quote).");
        var (code, output) = Ssh.RunsWindows(target)
            ? await Ssh.RunAsync(target, WindowsCommand(url, api), null, TimeSpan.FromMinutes(10))
            : await Ssh.RunScriptAsync(target, UnixScript(url, api), TimeSpan.FromMinutes(10));
        // What the server says is its own account (a server can say anything): the connection made again afterwards is
        // what shows LlamaTree is really there.
        if (code == 0) return $"{target} reports LlamaTree is installed. Connecting to it again…";
        throw new InvalidOperationException($"LlamaTree wasn't installed on {target}. What the server said:\n{(output.Length > 0 ? output : $"(nothing; ssh ended with {code})")}");
    }

    private static string UnixScript(string url, string? api)
    {
        var forward = api is { Length: > 0 } ? $"LLAMATREE_UPDATE_URL='{api}'; export LLAMATREE_UPDATE_URL; " : "";
        return $"""
            set -e
            {forward}d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
            if command -v curl >/dev/null 2>&1; then curl -fsSL -o "$d/install.sh" '{url}'
            elif command -v wget >/dev/null 2>&1; then wget -q -O "$d/install.sh" '{url}'
            else echo "LlamaTree: the server has neither curl nor wget to download it with." >&2; exit 1; fi
            sh "$d/install.sh" </dev/null
            """;
    }

    /// <summary>
    /// The command for a Windows server, as cmd runs it: Windows PowerShell (always there) runs the release's install.ps1,
    /// which fails by throwing, so PowerShell's exit code says whether it worked. TLS 1.2 first, which GitHub needs and
    /// Windows PowerShell may not offer by default.
    /// </summary>
    internal static string WindowsCommand(string url, string? api)
    {
        var script = url.EndsWith("/install.sh", StringComparison.Ordinal) ? url[..^"install.sh".Length] + "install.ps1" : url;
        var forward = api is { Length: > 0 } ? $"$env:LLAMATREE_UPDATE_URL='{api}'; " : "";
        return "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='Stop'; " +
               "[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072; " +
               $"{forward}irm '{script}' -UseBasicParsing | iex\"";
    }
}
