namespace LlamaTree.Hosting;

/// <summary>
/// Installs LlamaTree on a server that doesn't have it, with the release's own install.sh (see tools/install): run
/// over ssh with no terminal, it installs the package with sudo where sudo needs no password, and otherwise a copy of
/// your own in ~/.local/share/LlamaTree (which updates itself), and checks what it downloads against GitHub's SHA-256.
/// It's the latest release, which may be newer than this computer's LlamaTree; a server that ends up older says so
/// on its badge, where it can be updated.
/// </summary>
internal static class ServerInstall
{
    private const string ScriptUrl = "https://github.com/LlamachantTechnology/LlamaTree/releases/latest/download/install.sh";

    /// <summary>Installs it on <paramref name="target"/>. Returns what to tell you; throws with why it didn't.</summary>
    public static async Task<string> InstallAsync(string target)
    {
        // install.sh reads LLAMATREE_UPDATE_URL itself: a test server standing in for GitHub stands in for it there too.
        var url = Environment.GetEnvironmentVariable("LLAMATREE_INSTALL_SCRIPT_URL") is { Length: > 0 } test ? test : ScriptUrl;
        if (url.Contains('\'')) throw new InvalidOperationException("The install script's address isn't usable.");
        var api = Environment.GetEnvironmentVariable("LLAMATREE_UPDATE_URL");
        if (api != null && api.Contains('\'')) throw new InvalidOperationException("LLAMATREE_UPDATE_URL isn't usable (it holds a quote).");
        var forward = api is { Length: > 0 } ? $"LLAMATREE_UPDATE_URL='{api}'; export LLAMATREE_UPDATE_URL; " : "";
        var script = $"""
            set -e
            {forward}d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
            if command -v curl >/dev/null 2>&1; then curl -fsSL -o "$d/install.sh" '{url}'
            elif command -v wget >/dev/null 2>&1; then wget -q -O "$d/install.sh" '{url}'
            else echo "LlamaTree: the server has neither curl nor wget to download it with." >&2; exit 1; fi
            sh "$d/install.sh" </dev/null
            """;
        var (code, output) = await Ssh.RunScriptAsync(target, script, TimeSpan.FromMinutes(10));
        // What the server says is its own account (a server can say anything): the connection made again afterwards is
        // what shows LlamaTree is really there.
        if (code == 0) return $"{target} reports LlamaTree is installed. Connecting to it again…";
        throw new InvalidOperationException($"LlamaTree wasn't installed on {target}. What the server said:\n{(output.Length > 0 ? output : $"(nothing; ssh ended with {code})")}");
    }
}
