using System.Text.RegularExpressions;
using LlamaTree.Updates;

namespace LlamaTree.Hosting;

/// <summary>
/// Updates a server's LlamaTree to this computer's version: the release's .deb or .rpm, whichever the server installs
/// with (apt, dnf or zypper), for its processor. The server downloads it from GitHub itself, checks it against the
/// SHA-256 in the release's signed SHA256SUMS (verified here, see UpdateService.ReleaseFilesAsync), and installs it with sudo, but only where
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
        // Its setup can't replace a session host that's running in your desktop session there, which setup run over
        // SSH (in a session of its own) doesn't see: it's updated there, as any Windows install of LlamaTree is.
        if (Ssh.RunsWindows(target))
            throw new InvalidOperationException($"{target} runs Windows: update LlamaTree there (LlamaTree's menu, Check for updates), and its sessions here pick it up when it restarts.");
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
              x86_64|amd64) arch=x64; {{Var("deb_url", $"llamatree_{version}_amd64.deb", false)}}; {{Var("deb_sha", $"llamatree_{version}_amd64.deb", true)}}
                            {{Var("rpm_url", $"llamatree-{version}-1.x86_64.rpm", false)}}; {{Var("rpm_sha", $"llamatree-{version}-1.x86_64.rpm", true)}} ;;
              aarch64|arm64) arch=arm64; {{Var("deb_url", $"llamatree_{version}_arm64.deb", false)}}; {{Var("deb_sha", $"llamatree_{version}_arm64.deb", true)}}
                            {{Var("rpm_url", $"llamatree-{version}-1.aarch64.rpm", false)}}; {{Var("rpm_sha", $"llamatree-{version}-1.aarch64.rpm", true)}} ;;
              *) echo "LlamaTree has no package for $(uname -m) servers." >&2; exit 3 ;;
            esac
            if command -v apt-get >/dev/null 2>&1; then url=$deb_url; sha=$deb_sha; file=llamatree.deb; inst='apt-get install -y'; tool=apt
            elif command -v dnf >/dev/null 2>&1; then url=$rpm_url; sha=$rpm_sha; file=llamatree.rpm; inst='dnf install -y'; tool=dnf
            elif command -v zypper >/dev/null 2>&1; then url=$rpm_url; sha=$rpm_sha; file=llamatree.rpm; inst='zypper --non-interactive install --allow-unsigned-rpm'; tool=zypper
            else echo "The server has none of apt, dnf or zypper to install LlamaTree with." >&2; exit 3; fi
            [ -n "$url" ] && [ -n "$sha" ] || { echo "LlamaTree {{version}}'s release has no package for this server." >&2; exit 3; }
            d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
            if command -v curl >/dev/null 2>&1; then curl -fsSL -o "$d/$file" "$url"; else wget -q -O "$d/$file" "$url"; fi
            echo "$sha  $d/$file" | sha256sum -c --status || { echo "The download didn't match the checksum GitHub publishes for it, so nothing was installed." >&2; exit 4; }
            chmod 755 "$d"; chmod 644 "$d/$file"
            if [ "$(id -u)" = 0 ]; then $inst "$d/$file"
            elif sudo -n true 2>/dev/null; then sudo -n $inst "$d/$file"
            else echo "NEEDS_PASSWORD $tool $arch"; exit {{NeedsPassword}}; fi
            """;
        var (code, output) = await Ssh.RunScriptAsync(target, script, TimeSpan.FromMinutes(10));
        if (code == 0) return $"LlamaTree {version} is installed on {target}.";
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (code == NeedsPassword && PasswordCommand(last, version, files) is { } command)
            throw new InvalidOperationException($"sudo on {target} needs a password, which LlamaTree can't type. Run this there:\n{command}");
        throw new InvalidOperationException($"LlamaTree couldn't be updated on {target}:\n{(output.Length > 0 ? output : $"ssh ended with {code}.")}");
    }

    /// <summary>
    /// The command to run on a server whose sudo wants a password, from what the script said it found there
    /// ("NEEDS_PASSWORD apt|dnf|zypper x64|arm64"). The server's answer only picks among this computer's own
    /// choices: the address and hash are the release's as read here, the install command one of three written here.
    /// Anything else the server says (it may be anyone's) gives no command.
    /// </summary>
    internal static string? PasswordCommand(string said, Version version, IReadOnlyDictionary<string, (string Url, string Sha256)> files)
    {
        var parts = said.Split(' ');
        if (parts is not ["NEEDS_PASSWORD", var tool, var arch] || arch is not ("x64" or "arm64")) return null;
        var (install, file) = tool switch
        {
            "apt" => ("apt-get install -y", $"llamatree_{version}_{(arch == "x64" ? "amd64" : "arm64")}.deb"),
            "dnf" => ("dnf install -y", $"llamatree-{version}-1.{(arch == "x64" ? "x86_64" : "aarch64")}.rpm"),
            "zypper" => ("zypper --non-interactive install --allow-unsigned-rpm", $"llamatree-{version}-1.{(arch == "x64" ? "x86_64" : "aarch64")}.rpm"),
            _ => (null, null),
        };
        if (install == null || file == null || !files.TryGetValue(file, out var f)) return null;
        if (!(UpdateService.Testing ? TestUrl() : SafeUrl()).IsMatch(f.Url) || !Sha256().IsMatch(f.Sha256)) return null;
        var local = tool == "apt" ? "llamatree.deb" : "llamatree.rpm";
        return $"curl -fsSL -o {local} '{f.Url}' && echo '{f.Sha256}  {local}' | sha256sum -c && sudo {install} ./{local}";
    }
}
