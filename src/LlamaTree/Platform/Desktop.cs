using System.Diagnostics;
using LlamaTree.Terminal;

namespace LlamaTree.Platform;

/// <summary>
/// Handing files, folders and links to the desktop (Explorer, Finder, the Linux file manager and browser), and reading
/// files or an image off the clipboard on macOS and Linux (Windows reads its clipboard in its window, see MainForm).
/// </summary>
internal static class Desktop
{
    /// <summary>Opens a link, file or folder with what the desktop opens it with.</summary>
    public static void OpenExternal(string target)
    {
        try
        {
            // On a Mac a folder with an extension may be a bundle (Something.app, .prefPane...), which "open" would
            // launch or install: it's shown selected in Finder instead.
            if (OperatingSystem.IsMacOS() && Directory.Exists(target))
            {
                var real = PathText.FinalPath(target) ?? target;
                if (Path.GetExtension(PathText.TrimEnd(real)).Length > 0) Run("/usr/bin/open", "-R", real);
                else Run("/usr/bin/open", real);
            }
            else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });   // ShellExecute, open or xdg-open
        }
        catch (Exception)
        {
            // Nothing useful to do if the desktop can't open it.
        }
    }

    /// <summary>
    /// Opens a changed file the way the file manager would, except that anything the desktop would run (scripts,
    /// executables) is opened for editing instead: "Open" must never execute a file Claude just wrote.
    /// </summary>
    public static void OpenFileForViewing(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            // So many Mac types do something when opened (.terminal, .webloc, .command, .prefPane, .mobileconfig...) that
            // only types known to be shown, not run, go to their app; everything else opens in the default text
            // editor. Judged by what the path really is: a link named notes.txt can point at anything.
            var real = PathText.FinalPath(path) ?? path;
            if (!IsRunnable(real) && MacViewable.Contains(Path.GetExtension(real))) Run("/usr/bin/open", real);
            else Run("/usr/bin/open", "-t", real);
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            var real = PathText.FinalPath(path) ?? path;
            if (!IsRunnable(real))
            {
                OpenExternal(real);
                return;
            }
            // The desktop's text editor for plain text; failing that, the folder it's in rather than the file itself.
            if (Capture("xdg-mime", ["query", "default", "text/plain"])?.Trim() is { Length: > 0 } desktopFile &&
                desktopFile.EndsWith(".desktop", StringComparison.Ordinal) && Run("gtk-launch", desktopFile[..^8], path))
                return;
            OpenExternal(Path.GetDirectoryName(path) ?? path);
            return;
        }
        if (!IsRunnable(path))
        {
            OpenExternal(path);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No "edit" verb for this type (e.g. .exe, .py).
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { ArgumentList = { path } });
        }
    }

    // Script and interpreter types that Windows' own unsafe-type list (AssocIsDangerous) may not cover.
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".py", ".pyw", ".pyz", ".sh", ".bash", ".rb", ".pl", ".jar",
        ".appinstaller", ".appx", ".appxbundle", ".msix", ".msixbundle", ".application", ".appref-ms",
    };

    // macOS: types its own apps (Preview, TextEdit, QuickTime, Pages...) or an editor show without running anything.
    private static readonly HashSet<string> MacViewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rtf", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg",
        ".conf", ".css", ".scss", ".less", ".c", ".h", ".cc", ".cpp", ".hpp", ".m", ".mm", ".cs", ".java", ".kt", ".go", ".rs",
        ".swift", ".ts", ".tsx", ".jsx", ".vue", ".sql", ".diff", ".patch",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".ico", ".icns", ".svg", ".pdf",
        ".mp3", ".m4a", ".wav", ".aiff", ".flac", ".mp4", ".m4v", ".mov",
        ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt", ".pages", ".numbers", ".key", ".odt", ".ods", ".odp",
    };

    // What macOS and Linux desktops run when opened: shell scripts, launchers, installers and app bundles.
    private static readonly HashSet<string> UnixRunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyw", ".pyz", ".sh", ".bash", ".zsh", ".fish", ".csh", ".ksh", ".rb", ".pl", ".jar", ".js", ".mjs", ".command",
        ".tool", ".app", ".workflow", ".scpt", ".applescript", ".pkg", ".mpkg", ".dmg", ".desktop", ".appimage", ".run", ".bin",
        ".deb", ".rpm", ".flatpakref", ".snap", ".terminal", ".fileloc", ".inetloc", ".webloc", ".url", ".prefpane", ".saver",
        ".mobileconfig", ".shortcut", ".scptd", ".action", ".osax", ".bundle", ".plugin", ".kext", ".mpkg",
        // Windows programs, which Wine (or the like) would run.
        ".exe", ".com", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".scr", ".msc", ".lnk", ".reg",
    };

    // Programs that run the file they are handed rather than display it.
    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "py", "pyw", "node", "deno", "bun", "bash", "sh", "git-bash",
        "wsl", "java", "javaw", "perl", "ruby", "rubyw", "php", "lua", "tclsh", "wish", "rundll32", "regsvr32", "msiexec",
        "regedit", "dotnet", "autohotkey", "autohotkey32", "autohotkey64", "autohotkeyu32", "autohotkeyu64",
    };

    /// <summary>True if the desktop's default action for this file runs it rather than showing it.</summary>
    internal static bool IsRunnable(string path)
    {
        var ext = Path.GetExtension(path);
        if (!OperatingSystem.IsWindows())
        {
            // An executable file runs whatever it's called (a script with a #! line, a program).
            try
            {
                if ((File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0) return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { /* nothing there to run */ }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }   // can't tell: don't run it
            return ext.Length > 1 && UnixRunnableExtensions.Contains(ext);
        }
        if (ext.Length <= 1) return false;
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC";
        return ScriptExtensions.Contains(ext)
            || pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(ext, StringComparer.OrdinalIgnoreCase)
            || Native.AssocIsDangerous(ext)
            || DefaultActionRuns(Native.AssocQuery(Native.ASSOCSTR_COMMAND, ext), Native.AssocQuery(Native.ASSOCSTR_EXECUTABLE, ext));
    }

    /// <summary>
    /// Catches script types this machine has associated with an interpreter (or with the file itself, as for .exe)
    /// even when the extension is on none of the lists above.
    /// </summary>
    /// <param name="command">The default verb's command line, e.g. <c>"%1" %*</c>.</param>
    /// <param name="program">The program that command runs.</param>
    internal static bool DefaultActionRuns(string? command, string? program)
    {
        var cmd = command?.TrimStart() ?? "";
        if (cmd.StartsWith("\"%1\"", StringComparison.Ordinal) || cmd.StartsWith("%1", StringComparison.Ordinal)) return true;
        if (string.IsNullOrEmpty(program)) return false;
        var name = Path.GetFileNameWithoutExtension(program);
        return ScriptHosts.Contains(name) || name.StartsWith("python", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- clipboard (macOS, Linux)

    /// <summary>Where an image pasted into a terminal is saved, as a file the agent can attach. Saved images go after a week.</summary>
    public static string PastedImageFolder { get; } = Path.Combine(Path.GetTempPath(), "LlamaTree", "pasted-images");

    /// <summary>A new file in <see cref="PastedImageFolder"/> for a pasted image, clearing out week-old ones first.</summary>
    public static string NewPastedImagePath()
    {
        Directory.CreateDirectory(PastedImageFolder);
        foreach (var old in new DirectoryInfo(PastedImageFolder).EnumerateFiles("*.png").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)))
        {
            try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return Path.Combine(PastedImageFolder, $"image-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
    }

    /// <summary>
    /// macOS and Linux: the files copied in Finder or the file manager, or the image on the clipboard saved as a PNG.
    /// On Linux this needs wl-paste (Wayland) or xclip (X11); without them only text pastes.
    /// </summary>
    public static string[] UnixClipboardFiles()
    {
        try
        {
            return OperatingSystem.IsMacOS() ? MacClipboardFiles() : LinuxClipboardFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>macOS and Linux: the clipboard's text (pbpaste; wl-paste or xclip on Linux), or null.</summary>
    public static string? UnixClipboardText()
    {
        if (OperatingSystem.IsMacOS()) return Capture("/usr/bin/pbpaste", []);
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            ? Capture("wl-paste", ["--no-newline", "--type", "text/plain;charset=utf-8"]) ?? Capture("wl-paste", ["--no-newline"])
            : Capture("xclip", ["-selection", "clipboard", "-o"]);
    }

    private const string MacClipboardScript = """
        ObjC.import('AppKit');
        function run(argv) {
          const pb = $.NSPasteboard.generalPasteboard;
          const urls = pb.readObjectsForClassesOptions($([$.NSURL]), $({ NSPasteboardURLReadingFileURLsOnlyKey: true }));
          if (urls && !urls.isNil() && urls.count > 0) {
            const out = [];
            for (let i = 0; i < urls.count; i++) out.push(ObjC.unwrap(urls.objectAtIndex(i).path));
            return 'files\n' + out.join('\n');
          }
          let data = pb.dataForType('public.png');
          if (data.isNil()) {
            const tiff = pb.dataForType('public.tiff');
            if (tiff.isNil()) return '';
            data = $.NSBitmapImageRep.imageRepWithData(tiff).representationUsingTypeProperties(4 /* PNG */, $({}));
          }
          return data.writeToFileAtomically(argv[0], true) ? 'image' : '';
        }
        """;

    private static string[] MacClipboardFiles()
    {
        var image = NewPastedImagePath();
        var answer = Capture("/usr/bin/osascript", ["-l", "JavaScript", "-e", MacClipboardScript, image]);
        if (answer == null) return [];
        var lines = answer.TrimEnd('\n').Split('\n');
        return lines[0] switch
        {
            "files" => lines[1..].Where(l => l.Length > 0).ToArray(),
            "image" when File.Exists(image) => [image],
            _ => [],
        };
    }

    private static string[] LinuxClipboardFiles()
    {
        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        string[] Read(string type) => wayland ? ["wl-paste", "--no-newline", "--type", type] : ["xclip", "-selection", "clipboard", "-t", type, "-o"];
        var listing = wayland ? Capture("wl-paste", ["--list-types"]) : Capture("xclip", ["-selection", "clipboard", "-t", "TARGETS", "-o"]);
        if (listing == null) return [];
        var types = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (types.Contains("text/uri-list"))
        {
            var cmd = Read("text/uri-list");
            var uris = Capture(cmd[0], cmd[1..]) ?? "";
            var files = uris.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith('#'))
                .Select(l => Uri.TryCreate(l, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : null)
                .OfType<string>().ToArray();
            if (files.Length > 0) return files;
        }
        if (!types.Contains("image/png")) return [];
        var read = Read("image/png");
        var bytes = CaptureBytes(read[0], read[1..]);
        if (bytes is not { Length: > 0 }) return [];
        var path = NewPastedImagePath();
        File.WriteAllBytes(path, bytes);
        return [path];
    }

    // ---------------------------------------------------------------- running helper programs

    /// <summary>Starts a program and returns at once; false if it couldn't be started.</summary>
    private static bool Run(string program, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A program's output as text, or null if it can't be run, fails or takes more than five seconds.</summary>
    internal static string? Capture(string program, string[] args) =>
        CaptureBytes(program, args) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;

    private static byte[]? CaptureBytes(string program, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.StandardInput.Close();
            _ = p.StandardError.ReadToEndAsync();
            using var buffer = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(buffer);
            if (!copy.Wait(TimeSpan.FromSeconds(5)) || !p.WaitForExit(1000))
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return null;
            }
            return p.ExitCode == 0 ? buffer.ToArray() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException)
        {
            return null;
        }
    }
}
