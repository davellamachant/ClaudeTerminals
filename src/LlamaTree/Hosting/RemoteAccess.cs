using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace LlamaTree.Hosting;

/// <summary>
/// "Let my other computers connect": this Windows computer as a server for LlamaTree elsewhere (see docs/servers.md),
/// through Windows' own OpenSSH Server. Turning it on installs and starts that, opens it in the firewall and (by
/// choice) turns off password sign-in; keys from your other computers are added where sshd reads them, which for an
/// administrator is a file only administrators can change. Each of those needs administrator rights, so each asks
/// Windows (one prompt) and runs as one elevated PowerShell, its script on its command line rather than in a file
/// something else could change before it runs.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RemoteAccess
{
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string SshdConfig => Path.Combine(ProgramData, "ssh", "sshd_config");
    private static string AdminKeys => Path.Combine(ProgramData, "ssh", "administrators_authorized_keys");
    private static string UserKeys => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys");

    /// <summary>Keys added here (an administrator's file can't be read without asking): their fingerprints and comments.</summary>
    private static string AddedKeysFile => Path.Combine(AppProfile.LocalFolder, "remote-access-keys.json");

    // ---------------------------------------------------------------- what's on

    public static object State()
    {
        using var me = WindowsIdentity.GetCurrent();
        var admin = IsAdministrator(me);
        var (installed, running, automatic) = Service();
        return new
        {
            account = me.Name,
            login = LoginName(me.Name),
            entra = me.Name.StartsWith(@"AzureAD\", StringComparison.OrdinalIgnoreCase),
            admin,
            computer = Environment.MachineName,
            installed,
            running,
            automatic,
            passwordSignIn = PasswordSignIn(),
            defaultShell = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\OpenSSH", "DefaultShell", null) as string,
            addresses = Addresses(),
            otherRules = OtherSshRules() ?? [],
            keys = (admin ? AddedKeys() : FileKeys(UserKeys)).Select(k => new { fingerprint = k.Fingerprint, comment = k.Comment }),
            myKey = MyKey(),
        };
    }

    /// <summary>
    /// Whether you're an administrator: in the Administrators group, also when (as usual) your token only holds it for
    /// denying, until you elevate.
    /// </summary>
    internal static bool IsAdministrator(WindowsIdentity me)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        return me.Groups?.Contains(admins) == true ||
               me.Claims.Any(c => c.Type == ClaimTypes.DenyOnlySid && c.Value == admins.Value);
    }

    /// <summary>The name to sign in with over SSH: the user's own (a local or Microsoft account), or domain\user.</summary>
    internal static string LoginName(string account)
    {
        var slash = account.IndexOf('\\');
        if (slash < 0) return account;
        var domain = account[..slash];
        return domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? account[(slash + 1)..] : account;
    }

    /// <summary>OpenSSH Server's service: there at all, running, started with Windows.</summary>
    private static (bool Installed, bool Running, bool Automatic) Service()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\sshd");
        if (key == null) return (false, false, false);
        var automatic = key.GetValue("Start") is int start && start == 2;
        return (true, ServiceRunning("sshd"), automatic);
    }

    /// <summary>Whether a service runs, by sc's state number (its words are in Windows' language).</summary>
    private static bool ServiceRunning(string name)
    {
        var (code, output) = Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), "query", name);
        return code == 0 && output.Split('\n').Any(l => l.Contains("STATE", StringComparison.Ordinal) && l.Contains(": 4 ", StringComparison.Ordinal));
    }

    /// <summary>Whether sshd lets you sign in with a password (its first PasswordAuthentication, outside a Match, counts), or null if its settings can't be read.</summary>
    internal static bool? PasswordSignIn(string? config = null)
    {
        try
        {
            foreach (var raw in config?.Split('\n') ?? File.ReadAllLines(SshdConfig))
            {
                var line = raw.Trim();
                if (line.StartsWith("Match", StringComparison.OrdinalIgnoreCase)) break;
                if (line.StartsWith("PasswordAuthentication", StringComparison.OrdinalIgnoreCase))
                    return !line["PasswordAuthentication".Length..].Trim().Equals("no", StringComparison.OrdinalIgnoreCase);
            }
            return true;   // sshd's default
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The display name of the firewall rule OpenSSH Server comes with (OpenSSH-Server-In-TCP), the one Turn on and Turn off change.</summary>
    internal const string OwnRule = "OpenSSH SSH Server (sshd)";

    /// <summary>
    /// The display names of the other enabled firewall rules that let TCP port 22 in, which Turn off leaves on: one you
    /// made yourself, say. Read through the firewall's COM interface, which needs no administrator rights and gives rules
    /// by display name. Null if the firewall can't be read.
    /// </summary>
    internal static List<string>? OtherSshRules()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
            var found = new List<string>();
            foreach (dynamic rule in policy.Rules)
            {
                // Inbound (1), allowing (1), on, and not OpenSSH's own.
                if (!(bool)rule.Enabled || (int)rule.Direction != 1 || (int)rule.Action != 1) continue;
                string name = rule.Name ?? "";
                if (name == OwnRule) continue;
                if (LetsSshIn((int)rule.Protocol, (string?)rule.LocalPorts, (string?)rule.ApplicationName, (string?)rule.serviceName))
                    found.Add(name);
            }
            return found.Distinct().Take(5).ToList();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or TypeLoadException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a rule lets sshd in: for TCP (6), naming port 22 itself (alone, in a list or in a range), and not kept to
    /// another program or service. Rules for every port are left out: those are nearly always one app's own (a Store
    /// app's, which the rule's app package keeps to it).
    /// </summary>
    internal static bool LetsSshIn(int protocol, string? localPorts, string? application, string? service)
    {
        if (protocol != 6) return false;
        if (!string.IsNullOrEmpty(application) && !Path.GetFileName(application).Equals("sshd.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(service) && service != "*" && !service.Equals("sshd", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var part in (localPorts ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "22") return true;
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var from) && int.TryParse(part[(dash + 1)..], out var to) && from <= 22 && 22 <= to) return true;
        }
        return false;
    }

    /// <summary>This computer's addresses another could reach it at: its networks', and Tailscale's (100.64.0.0/10) marked as such.</summary>
    private static object[] Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        !n.Description.Contains("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase))   // WSL's and VMs' own, inside this PC
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => (n, a.Address)))
            .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !x.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(x =>
            {
                var b = x.Address.GetAddressBytes();
                var tailscale = b[0] == 100 && b[1] >= 64 && b[1] < 128;
                return (object)new { address = x.Address.ToString(), network = x.n.Name, tailscale };
            })
            .ToArray();

    // ---------------------------------------------------------------- keys

    internal sealed record PublicKey(string Line, string Blob, string Fingerprint, string Comment);

    private static readonly string[] KeyTypes =
    [
        "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com",
    ];

    /// <summary>
    /// A public key as pasted (the one line of an id_*.pub file), or null if it isn't one: a known type, its data
    /// really a key of that type, no options in front (from=, command=…), and a comment kept only when it's plain
    /// text. What's returned is rebuilt from those parts, so nothing else pasted with it goes anywhere.
    /// </summary>
    internal static PublicKey? ParseKey(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1) return null;
        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !KeyTypes.Contains(parts[0], StringComparer.Ordinal)) return null;
        byte[] data;
        try { data = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { return null; }
        // A key's data starts with its type: a 4-byte length, then the name.
        if (data.Length < 4) return null;
        var length = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        if (length != parts[0].Length || data.Length < 4 + length || Encoding.ASCII.GetString(data, 4, length) != parts[0]) return null;
        var comment = parts.Length > 2 ? parts[2].Trim() : "";
        if (comment.Length > 100 || comment.Any(c => c < 0x20 || c > 0x7e || c == '\'' || c == '"' || c == '`')) comment = "";
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(data)).TrimEnd('=');
        return new PublicKey(comment.Length > 0 ? $"{parts[0]} {parts[1]} {comment}" : $"{parts[0]} {parts[1]}", parts[1], fingerprint, comment);
    }

    private static List<PublicKey> FileKeys(string path)
    {
        try { return File.ReadAllLines(path).Select(ParseKey).OfType<PublicKey>().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private sealed record AddedKey(string Fingerprint, string Comment, string Blob);

    private static List<PublicKey> AddedKeys()
    {
        try
        {
            var added = JsonSerializer.Deserialize<List<AddedKey>>(File.ReadAllText(AddedKeysFile)) ?? [];
            return added.Select(a => new PublicKey("", a.Blob, a.Fingerprint, a.Comment)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static void SaveAddedKeys(IEnumerable<PublicKey> keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AddedKeysFile)!);
        File.WriteAllText(AddedKeysFile, JsonSerializer.Serialize(keys.Select(k => new AddedKey(k.Fingerprint, k.Comment, k.Blob)).ToList()));
    }

    /// <summary>Lets the key pasted sign in as you. Returns what to tell you; throws with why it didn't.</summary>
    public static async Task<string> AddKeyAsync(string text)
    {
        var key = ParseKey(text) ?? throw new InvalidOperationException("That isn't a public key. Paste the one line of the other computer's .pub file (for example ~/.ssh/id_ed25519.pub), starting ssh-ed25519.");
        using var me = WindowsIdentity.GetCurrent();
        if (IsAdministrator(me))
        {
            await RunElevatedAsync(AddAdminKeyScript(key));
            SaveAddedKeys(AddedKeys().Where(k => k.Blob != key.Blob).Append(key));
        }
        else
        {
            var lines = File.Exists(UserKeys) ? File.ReadAllLines(UserKeys).ToList() : [];
            if (!lines.Any(l => l.Contains(key.Blob, StringComparison.Ordinal)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UserKeys)!);
                File.WriteAllLines(UserKeys, lines.Append(key.Line), Encoding.ASCII);
            }
        }
        return key.Comment.Length > 0 ? $"{key.Comment} can sign in now." : $"The key {key.Fingerprint} can sign in now.";
    }

    /// <summary>Stops the key with <paramref name="fingerprint"/> signing in as you.</summary>
    public static async Task RemoveKeyAsync(string fingerprint)
    {
        using var me = WindowsIdentity.GetCurrent();
        if (IsAdministrator(me))
        {
            var key = AddedKeys().FirstOrDefault(k => k.Fingerprint == fingerprint) ?? throw new InvalidOperationException("LlamaTree didn't add that key.");
            await RunElevatedAsync(RemoveAdminKeyScript(key.Blob));
            SaveAddedKeys(AddedKeys().Where(k => k.Fingerprint != fingerprint));
        }
        else if (File.Exists(UserKeys))
        {
            var kept = File.ReadAllLines(UserKeys).Where(l => ParseKey(l)?.Fingerprint != fingerprint).ToArray();
            File.WriteAllLines(UserKeys, kept, Encoding.ASCII);
        }
    }

    /// <summary>
    /// An administrator's key added: only administrators may change their file, and Windows ignores it unless only they
    /// and SYSTEM can (sshd reads it as ASCII or UTF-8, not Windows PowerShell's UTF-16).
    /// </summary>
    internal static string AddAdminKeyScript(PublicKey key) => $$"""
        $f = {{Quote(AdminKeys)}}
        if (-not (Test-Path $f) -or -not (Select-String -Path $f -SimpleMatch {{Quote(key.Blob)}} -Quiet)) { Add-Content -Path $f -Value {{Quote(key.Line)}} -Encoding ascii }
        icacls $f /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
        if ($LASTEXITCODE) { throw "Its permissions couldn't be set (icacls: $LASTEXITCODE)." }
        """;

    internal static string RemoveAdminKeyScript(string blob) => $$"""
        $f = {{Quote(AdminKeys)}}
        if (Test-Path $f) { Set-Content -Path $f -Value @(Get-Content $f | Where-Object { -not $_.Contains({{Quote(blob)}}) }) -Encoding ascii }
        """;

    // ---------------------------------------------------------------- on and off

    /// <summary>
    /// Turns it on: OpenSSH Server installed (Windows' own, from Windows Update), started now and with Windows, its
    /// firewall rule on for private networks (and public ones, when asked: a cloud VM's is one), and password sign-in
    /// off when <paramref name="keysOnly"/> (LlamaTree only uses keys; a password can be guessed).
    /// </summary>
    public static Task EnableAsync(bool keysOnly, bool publicNetworks) => RunElevatedAsync(EnableScript(keysOnly, publicNetworks));

    /// <summary>
    /// The script for <see cref="EnableAsync"/>. Password sign-in is turned off with a line put first in sshd's settings
    /// (its first value counts), unless the first already says so; sshd then restarts.
    /// </summary>
    internal static string EnableScript(bool keysOnly, bool publicNetworks)
    {
        var profiles = publicNetworks ? "Domain,Private,Public" : "Domain,Private";
        var passwords = keysOnly ? $$"""
            $c = {{Quote(SshdConfig)}}
            $first = Get-Content $c | Where-Object { $_ -match '^\s*(PasswordAuthentication|Match)\b' } | Select-Object -First 1
            if ($first -notmatch '^\s*PasswordAuthentication\s+no\s*$') { Set-Content -Path $c -Value (@('PasswordAuthentication no') + (Get-Content $c)) -Encoding ascii }
            Restart-Service sshd
            """ : "";
        return $$"""
            $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
            if (-not $cap) { throw 'This Windows has no OpenSSH Server to install.' }
            if ($cap.State -ne 'Installed') { Add-WindowsCapability -Online -Name $cap.Name | Out-Null }
            Set-Service sshd -StartupType Automatic
            Start-Service sshd
            if (-not (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName {{Quote(OwnRule)}} -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow | Out-Null
            }
            Set-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -Enabled True -Profile {{profiles}}
            {{passwords}}
            """;
    }

    /// <summary>Turns it off: OpenSSH Server stopped and not started with Windows, its firewall rule off. Keys stay, for turning it on again.</summary>
    public static Task DisableAsync() => RunElevatedAsync(DisableScript);

    internal const string DisableScript = """
        Stop-Service sshd -ErrorAction SilentlyContinue
        Set-Service sshd -StartupType Manual -ErrorAction SilentlyContinue
        Disable-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
        """;

    /// <summary>A PowerShell string literal of <paramref name="text"/>: in single quotes, which take nothing in it as code, its own doubled.</summary>
    internal static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>A script as it's run elevated: stopping at the first error, saying how it went in <paramref name="result"/>.</summary>
    internal static string Wrapped(string script, string result) => $$"""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        try {
        {{script}}
            Set-Content -Path {{Quote(result)}} -Value 'OK'
        } catch {
            Set-Content -Path {{Quote(result)}} -Value ('FAILED: ' + $_.Exception.Message)
        }
        """;

    /// <summary>
    /// Runs <paramref name="script"/> in an elevated Windows PowerShell, Windows asking you first. It says how it went in
    /// a file of yours; throws with why it didn't (or that you said no).
    /// </summary>
    private static async Task RunElevatedAsync(string script)
    {
        var result = Path.Combine(Path.GetTempPath(), $"llamatree-remote-access-{Guid.NewGuid():N}.txt");
        File.WriteAllText(result, "");
        var wrapped = Wrapped(script, result);
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)),
        };
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Windows PowerShell didn't start.");
            await p.WaitForExitAsync();
            var said = File.ReadAllText(result).Trim();
            if (said == "OK") return;
            throw new InvalidOperationException(said.StartsWith("FAILED: ", StringComparison.Ordinal) ? said["FAILED: ".Length..] : "It didn't finish.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Nothing was changed: Windows asked for permission, and it wasn't given.");
        }
        finally { try { File.Delete(result); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    // ---------------------------------------------------------------- this computer's own key, for connecting elsewhere

    private static string SshFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>This computer's public key (the first of the usual ones there is), to add on the computers it connects to; null if it has none.</summary>
    public static object? MyKey()
    {
        foreach (var name in new[] { "id_ed25519.pub", "id_ecdsa.pub", "id_rsa.pub" })
        {
            var path = Path.Combine(SshFolder, name);
            try { if (File.Exists(path) && ParseKey(File.ReadAllText(path)) is { } key) return new { path, text = key.Line, fingerprint = key.Fingerprint }; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>
    /// Makes this computer a key (ed25519, ~/.ssh/id_ed25519) when it has none. With no passphrase: LlamaTree can't type
    /// one (an ssh-agent holding the key is the way to have both).
    /// </summary>
    public static object MakeMyKey()
    {
        if (MyKey() is { } existing) return existing;
        Directory.CreateDirectory(SshFolder);
        var (code, output) = Run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", $"{Environment.UserName}@{Environment.MachineName}", "-f", Path.Combine(SshFolder, "id_ed25519"));
        if (code != 0) throw new InvalidOperationException("ssh-keygen didn't make a key: " + output);
        return MyKey() ?? throw new InvalidOperationException("ssh-keygen didn't make a key.");
    }

    private static (int Code, string Output) Run(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (InvalidOperationException) { } return (-1, ""); }
            return (p.ExitCode, (output.Result + errors.Result).Trim());
        }
        catch (System.ComponentModel.Win32Exception ex) { return (-1, ex.Message); }
    }
}
