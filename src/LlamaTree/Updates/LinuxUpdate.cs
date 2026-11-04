using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using LlamaTree.Terminal;

namespace LlamaTree.Updates;

/// <summary>How this copy of LlamaTree was installed, which decides how it updates itself.</summary>
public enum InstallKind
{
    /// <summary>Can't update itself (macOS, run from source, or a Linux copy it can't replace): the release page is linked.</summary>
    None,
    /// <summary>The Windows installer.</summary>
    Windows,
    /// <summary>The .deb package: apt installs the new one.</summary>
    Deb,
    /// <summary>The .rpm package: dnf, zypper or rpm installs the new one.</summary>
    Rpm,
    /// <summary>The .tar.gz, unpacked into a folder this user can write to: the folder is swapped for the new one.</summary>
    Tar,
}

/// <summary>
/// Linux updates. The packages carry a <c>package-type</c> file (see tools/package-linux.cs) saying which they are.
/// A package is installed while the app is still open, through pkexec (the desktop's administrator password prompt),
/// so a cancelled prompt or a failure is shown in the update dialog; the app then closes and reopens. An unpacked
/// .tar.gz is unpacked again beside the app's folder, and the folders are swapped once the app has closed.
/// </summary>
internal static class LinuxUpdate
{
    /// <summary>The folders searched for the tools, besides PATH: a copy started from the desktop may have a short PATH.</summary>
    private const string SystemPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    private static string AppDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static InstallKind Detect()
    {
        string? type;
        try { type = File.ReadAllText(Path.Combine(AppDir, "package-type")).Trim(); }
        catch (IOException) { return InstallKind.None; }
        catch (UnauthorizedAccessException) { return InstallKind.None; }
        return type switch
        {
            "deb" when Find("pkexec") != null && (Find("apt-get") ?? Find("dpkg")) != null => InstallKind.Deb,
            "rpm" when Find("pkexec") != null && (Find("dnf") ?? Find("zypper") ?? Find("rpm")) != null => InstallKind.Rpm,
            // The folder is replaced, so both it and the folder it's in must be this user's to change.
            "tar" when Writable(AppDir) && Path.GetDirectoryName(AppDir) is { } parent && Writable(parent) => InstallKind.Tar,
            _ => InstallKind.None,
        };
    }

    /// <summary>The release asset for this kind of install on this machine, or null when there isn't one.</summary>
    public static string? AssetName(InstallKind kind, Version version)
    {
        var (deb, rpm, arch) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => ("amd64", "x86_64", "x64"),
            Architecture.Arm64 => ("arm64", "aarch64", "arm64"),
            _ => (null, null, null),
        };
        if (arch == null) return null;
        return kind switch
        {
            InstallKind.Deb => $"llamatree_{version}_{deb}.deb",
            InstallKind.Rpm => $"llamatree-{version}-1.{rpm}.rpm",
            InstallKind.Tar => $"LlamaTree-{version}-linux-{arch}.tar.gz",
            _ => null,
        };
    }

    /// <summary>
    /// Installs the downloaded package, or unpacks the .tar.gz beside the app, while the app is still open. Returns
    /// the shell commands that finish the update once the app has closed (swap the folders, if any) and reopen it.
    /// </summary>
    public static async Task<string> PrepareAsync(InstallKind kind, string file, CancellationToken ct = default)
    {
        switch (kind)
        {
            case InstallKind.Deb:
                // apt pulls in any new dependencies; DEBIAN_FRONTEND keeps it from stopping to ask anything.
                if (Find("apt-get") is { } apt) await AsAdminAsync(["env", "DEBIAN_FRONTEND=noninteractive", apt, "install", "-y", file], ct);
                else await AsAdminAsync([Find("dpkg")!, "-i", file], ct);
                return Launch(Path.Combine(AppDir, "LlamaTree"));
            case InstallKind.Rpm:
                // The package isn't signed: dnf installs a local file without a key; zypper has to be told it may.
                if (Find("dnf") is { } dnf) await AsAdminAsync([dnf, "install", "-y", file], ct);
                else if (Find("zypper") is { } zypper) await AsAdminAsync([zypper, "--non-interactive", "install", "--allow-unsigned-rpm", file], ct);
                else await AsAdminAsync([Find("rpm")!, "-U", file], ct);
                return Launch(Path.Combine(AppDir, "LlamaTree"));
            case InstallKind.Tar:
                return await UnpackBesideAsync(file, ct);
            default:
                throw new InvalidOperationException("This copy of LlamaTree can't update itself: get the new version from its release page.");
        }
    }

    private static async Task<string> UnpackBesideAsync(string file, CancellationToken ct)
    {
        var app = AppDir;
        var parent = Path.GetDirectoryName(app)!;
        var staging = Path.Combine(parent, ".llamatree-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            await using (var stream = File.OpenRead(file))
            await using (var gz = new GZipStream(stream, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gz, staging, overwriteFiles: false, ct);
            // The archive holds one folder, LlamaTree-<version>-linux-<arch>, with the app in it.
            var unpacked = Directory.GetDirectories(staging) is [var only] ? only : null;
            if (unpacked == null || !File.Exists(Path.Combine(unpacked, "LlamaTree")))
                throw new InvalidOperationException("The downloaded update isn't laid out as expected, so it wasn't installed.");
            var old = Path.Combine(staging, "previous");
            // The folder keeps its name (and so any launcher pointing into it); a failed swap reopens the copy there was.
            var q = CommandLineResolver.ShellQuote;
            return $"if mv {q(app)} {q(old)}; then mv {q(unpacked)} {q(app)} || mv {q(old)} {q(app)}; fi; " +
                   $"rm -rf {q(staging)}; " + Launch(Path.Combine(app, "LlamaTree"));
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static string Launch(string program) =>
        $"cd {CommandLineResolver.ShellQuote(Path.GetDirectoryName(program)!)} && exec {CommandLineResolver.ShellQuote(program)} </dev/null >/dev/null 2>&1";

    /// <summary>
    /// Runs <paramref name="finish"/> in a separate session once <paramref name="processes"/> (the session host and its
    /// window) have exited, at most two minutes on.
    /// </summary>
    public static void FinishAfterExit(string finish, int[] processes)
    {
        // An exited process its parent hasn't reaped yet still answers kill -0, so a zombie (state Z) counts as gone.
        static string Alive(int pid) => $"(kill -0 {pid} 2>/dev/null && [ \"$(cut -d' ' -f3 /proc/{pid}/stat 2>/dev/null)\" != Z ])";
        var script = $"i=0; while {{ {string.Join(" || ", processes.Select(Alive))}; }} && [ $i -lt 600 ]; " +
                     $"do sleep 0.2; i=$((i+1)); done; {finish}";
        // Its own session, so nothing that ends the app's process group ends this too.
        var psi = Find("setsid") is { } setsid ? new ProcessStartInfo(setsid) : new ProcessStartInfo("/bin/sh");
        if (psi.FileName != "/bin/sh") psi.ArgumentList.Add("/bin/sh");
        foreach (var a in new[] { "-c", script }) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = false;
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Runs a command as root through pkexec, which asks for an administrator's password in a desktop dialog.</summary>
    private static async Task AsAdminAsync(string[] command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Find("pkexec")!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in command) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("pkexec didn't start.");
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var errors = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("The update took too long to install and was stopped.");
        }
        if (p.ExitCode == 0) return;
        // pkexec's own: 126 when the password prompt is dismissed, 127 when not authorised.
        if (p.ExitCode is 126 or 127)
            throw new InvalidOperationException("The update wasn't installed: installing it needs an administrator's password.");
        var detail = string.Join('\n', ((await errors).Trim() is { Length: > 0 } e ? e : (await output).Trim())
            .Split('\n').TakeLast(6));
        throw new InvalidOperationException($"The update didn't install ({Path.GetFileName(command[0] == "env" ? command[2] : command[0])} exit code {p.ExitCode}).\n{detail}");
    }

    private static string? Find(string program) =>
        CommandLineResolver.FindUnixProgram(program, (Environment.GetEnvironmentVariable("PATH") ?? "") + ":" + SystemPath);

    private static bool Writable(string folder)
    {
        var probe = Path.Combine(folder, ".llamatree-write-test-" + Environment.ProcessId);
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
