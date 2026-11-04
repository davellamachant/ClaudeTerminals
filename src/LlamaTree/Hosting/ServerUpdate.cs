using System.Text.RegularExpressions;
using LlamaTree.Updates;

namespace LlamaTree.Hosting;

/// <summary>
/// Updates a server's LlamaTree to this computer's version: the release's .deb or .rpm, whichever the server installs
/// with (apt, dnf or zypper), for its processor. The server downloads it from GitHub itself, checks it against the
/// SHA-256 GitHub publishes for the file (as this computer's updater does), and installs it with sudo, but only where
/// sudo needs no password: there's nowhere to type one. Otherwise it says what to run there.
/// </summary>
internal static partial class ServerUpdate
{
    /// <summary>The script's exit code when sudo wants a password; its last line then says what to run.</summary>
    private const int NeedsPassword = 5;

    // An address that can sit in quotes in the script: no quote, space or anything a shell would act on there. Plain
    // http only from a local test server standing in for GitHub (LLAMATREE_UPDATE_URL).
    [GeneratedRegex("^https://[A-Za-z0-9._~:/?=&%+-]+$")]
    private static partial Regex SafeUrl();

    [GeneratedRegex("^https?://[A-Za-z0-9._~:/?=&%+-]+$")]
    private static partial Regex TestUrl();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    /// <summary>Installs <paramref name="version"/> on <paramref name="target"/>. Returns what to tell you; throws with why it didn't.</summary>
    public static async Task<string> UpdateAsync(string target, Version version)
    {
        var files = await UpdateService.ReleaseFilesAsync(version);
        // Only addresses and hashes that are plainly that go into the script.
        string Var(string name, string file, bool hash)
        {
            var value = files.TryGetValue(file, out var f) ? (hash ? f.Sha256 : f.Url) : "";
            if (value.Length > 0 && !(hash ? Sha256() : UpdateService.Testing ? TestUrl() : SafeUrl()).IsMatch(value)) value = "";
            return $"{name}='{value}'";
        }
        var script = $$"""
            set -e
            case "$(uname -m)" in
              x86_64|amd64) {{Var("deb_url", $"llamatree_{version}_amd64.deb", false)}}; {{Var("deb_sha", $"llamatree_{version}_amd64.deb", true)}}
                            {{Var("rpm_url", $"llamatree-{version}-1.x86_64.rpm", false)}}; {{Var("rpm_sha", $"llamatree-{version}-1.x86_64.rpm", true)}} ;;
              aarch64|arm64) {{Var("deb_url", $"llamatree_{version}_arm64.deb", false)}}; {{Var("deb_sha", $"llamatree_{version}_arm64.deb", true)}}
                            {{Var("rpm_url", $"llamatree-{version}-1.aarch64.rpm", false)}}; {{Var("rpm_sha", $"llamatree-{version}-1.aarch64.rpm", true)}} ;;
              *) echo "LlamaTree has no package for $(uname -m) servers." >&2; exit 3 ;;
            esac
            if command -v apt-get >/dev/null 2>&1; then url=$deb_url; sha=$deb_sha; file=llamatree.deb; inst='apt-get install -y'
            elif command -v dnf >/dev/null 2>&1; then url=$rpm_url; sha=$rpm_sha; file=llamatree.rpm; inst='dnf install -y'
            elif command -v zypper >/dev/null 2>&1; then url=$rpm_url; sha=$rpm_sha; file=llamatree.rpm; inst='zypper --non-interactive install --allow-unsigned-rpm'
            else echo "The server has none of apt, dnf or zypper to install LlamaTree with." >&2; exit 3; fi
            [ -n "$url" ] && [ -n "$sha" ] || { echo "LlamaTree {{version}}'s release has no package for this server." >&2; exit 3; }
            d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
            if command -v curl >/dev/null 2>&1; then curl -fsSL -o "$d/$file" "$url"; else wget -q -O "$d/$file" "$url"; fi
            echo "$sha  $d/$file" | sha256sum -c --status || { echo "The download didn't match the checksum GitHub publishes for it, so nothing was installed." >&2; exit 4; }
            chmod 755 "$d"; chmod 644 "$d/$file"
            if [ "$(id -u)" = 0 ]; then $inst "$d/$file"
            elif sudo -n true 2>/dev/null; then sudo -n $inst "$d/$file"
            else echo "NEEDS_PASSWORD $url $sha $inst"; exit {{NeedsPassword}}; fi
            """;
        var (code, output) = await Ssh.RunScriptAsync(target, script, TimeSpan.FromMinutes(10));
        if (code == 0) return $"LlamaTree {version} is installed on {target}.";
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (code == NeedsPassword && last.StartsWith("NEEDS_PASSWORD ", StringComparison.Ordinal))
        {
            var parts = last.Split(' ', 4);   // NEEDS_PASSWORD, the address, its SHA-256, the install command
            var file = parts[1].EndsWith(".deb", StringComparison.Ordinal) ? "llamatree.deb" : "llamatree.rpm";
            throw new InvalidOperationException($"sudo on {target} needs a password, which LlamaTree can't type. Run this there:\n" +
                $"curl -fsSL -o {file} {parts[1]} && echo '{parts[2]}  {file}' | sha256sum -c && sudo {parts[3]} ./{file}");
        }
        throw new InvalidOperationException($"LlamaTree couldn't be updated on {target}:\n{(output.Length > 0 ? output : $"ssh ended with {code}.")}");
    }
}
