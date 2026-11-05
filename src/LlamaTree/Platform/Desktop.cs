using System.Diagnostics;
using LlamaTree.Terminal;

namespace LlamaTree.Platform;

/// <summary>
/// Handing files, folders and links to the desktop (Explorer, Finder, the Linux file manager and browser), and reading
/// files or an image off the clipboard on macOS and Linux (Windows reads its clipboard in its window, see MainForm).
/// </summary>
internal static partial class Desktop
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

    /// <summary>Opens the folder a file or folder is in, with it selected (Explorer, Finder, or the Linux file manager).</summary>
    public static void ShowInFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Explorer reads its own command line: /select, then the path, quoted (a Windows path can't hold a quote).
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { }
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            Run("/usr/bin/open", "-R", path);
            return;
        }
        // The freedesktop file manager interface (Nautilus, Dolphin, Nemo, Caja, Thunar...) selects it; failing that,
        // the folder is opened. A comma would split dbus-send's array, so it's escaped like the rest of the URI.
        var uri = new Uri(path).AbsoluteUri.Replace(",", "%2C");
        if (Capture("dbus-send", ["--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems", $"array:string:{uri}", "string:"]) != null)
            return;
        OpenExternal(Path.GetDirectoryName(PathText.TrimEnd(path)) ?? path);
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

    /// <summary>
    /// A new file for an image pasted into a terminal, to hand the agent; week-old ones are cleared out first. Kept where
    /// only you can reach: on Linux the session's runtime folder ($XDG_RUNTIME_DIR, gone at logout) when there's a sound
    /// one, else the app's local data folder. Never the shared temp folder, where another account could make the folder
    /// first (or a link in its place) to read the pictures or have the clear-out delete files elsewhere. Throws
    /// <see cref="IOException"/> when no such folder can be had: the paste fails rather than writing somewhere unsafe.
    /// </summary>
    public static string NewPastedImagePath() =>
        NewPastedImagePath(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), AppProfile.CustomFolder ?? AppProfile.LocalFolder, DateTime.Now);

    internal static string NewPastedImagePath(string? runtimeFolder, string dataFolder, DateTime now)
    {
        foreach (var folder in PastedImageFolders(runtimeFolder, dataFolder))
        {
            if (!MakePrivateFolder(folder)) continue;
            ClearOldPastedImages(folder, now.ToUniversalTime().AddDays(-7));
            return Path.Combine(folder, $"image-{now:yyyyMMdd-HHmmss-fff}.png");
        }
        throw new IOException("The pasted image wasn't saved: its folder isn't one only you can use (it's a link, or another account's).");
    }

    /// <summary>Where pasted images may go, best first: the runtime folder's (macOS and Linux, when it's sound), then the data folder's.</summary>
    internal static IEnumerable<string> PastedImageFolders(string? runtimeFolder, string dataFolder)
    {
        if (!OperatingSystem.IsWindows() && runtimeFolder is { Length: > 0 } && Path.IsPathFullyQualified(runtimeFolder) && IsPrivate(runtimeFolder, OthersAny))
            yield return Path.Combine(runtimeFolder, "LlamaTree", "pasted-images");
        yield return Path.Combine(dataFolder, "pasted-images");
    }

    /// <summary>The names pasted images are given (see <see cref="NewPastedImagePath()"/>): the only files the clear-out deletes.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^image-\d{8}-\d{6}-\d{3}\.png$")]
    private static partial System.Text.RegularExpressions.Regex PastedImageName();

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OthersWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode OthersAny = OthersWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>
    /// Makes <paramref name="folder"/> and its parent if they're missing (0700 on macOS and Linux) and checks both are
    /// yours: real folders, not links; and, on macOS and Linux, owned by you and writable by no one else. The folder is
    /// then made 0700. False if they aren't yours, and nothing should be written there.
    /// </summary>
    internal static bool MakePrivateFolder(string folder)
    {
        try
        {
            var parent = Path.GetDirectoryName(folder)!;
            foreach (var f in new[] { parent, folder })   // the parent checked before anything's made in it
            {
                if (IsLink(f)) return false;   // not made through, or checked through
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(f);
                else if (!Directory.Exists(f)) Directory.CreateDirectory(f, OwnerOnly);
                if (!IsPrivate(f, OthersWrite)) return false;
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, OwnerOnly);   // yours, but others could read it: closed up
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// A real folder (not a link or other reparse point) that's yours: on macOS and Linux owned by you, and with none of
    /// <paramref name="refused"/> in its mode.
    /// </summary>
    private static bool IsPrivate(string folder, UnixFileMode refused)
    {
        try
        {
            if (IsLink(folder) || !Directory.Exists(folder)) return false;
            if (OperatingSystem.IsWindows()) return true;
            var mode = File.GetUnixFileMode(folder);
            if ((mode & refused) != 0) return false;
            // Only a file's owner may change its mode: setting it to what it is changes nothing, and fails for anyone else's.
            File.SetUnixFileMode(folder, mode);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A symbolic link (dangling or not), junction or other reparse point: not looked through.</summary>
    private static bool IsLink(string path)
    {
        var info = new FileInfo(path);   // for a folder too: neither looks through a link
        if (info.LinkTarget != null) return true;
        var attributes = info.Attributes;   // all bits set (-1) when nothing's there
        return (int)attributes != -1 && attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>
    /// Deletes the pasted images in <paramref name="folder"/> last written before <paramref name="before"/>: only files
    /// of the app's own naming, never links, and nothing in folders within.
    /// </summary>
    internal static void ClearOldPastedImages(string folder, DateTime before)
    {
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (entry is not FileInfo file || !PastedImageName().IsMatch(file.Name)) continue;
            try
            {
                if (file.LinkTarget != null || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LastWriteTimeUtc >= before) continue;
                file.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Creates a pasted image's file: a new one (never one that's there, or a link), readable only by you (0600 on macOS and Linux).</summary>
    public static FileStream CreatePastedImage(string path) => new(path, OperatingSystem.IsWindows()
        ? new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write }
        : new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });

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
        // The image's path is needed before it's known whether there is one: with nowhere safe to put it, copied files
        // still paste (an image is written nowhere, as "" can't be written to).
        string? image = null;
        try { image = NewPastedImagePath(); }
        catch (IOException) { }
        var answer = Capture("/usr/bin/osascript", ["-l", "JavaScript", "-e", MacClipboardScript, image ?? ""]);
        if (answer == null) return [];
        var lines = answer.TrimEnd('\n').Split('\n');
        switch (lines[0])
        {
            case "files":
                return lines[1..].Where(l => l.Length > 0).ToArray();
            case "image" when image != null && File.Exists(image):
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(image, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // written by the script: as the umask had it
                return [image];
            default:
                return [];
        }
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
        using (var file = CreatePastedImage(path)) file.Write(bytes);
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
