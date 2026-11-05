using System.Text;
using System.Text.Json;

namespace LlamaTree;

/// <summary>
/// The app's log: what went wrong that nobody was told, kept in a file for when something seems off. One file for each
/// kind of process (the window, the session host, the bridge on a server: see <see cref="Start"/>), so they never
/// write to the same one, in a "logs" folder in the app's local data. Each entry is one line: when (UTC), the process,
/// a level and what happened. A file past <see cref="MaxBytes"/> is moved to .1 (the one there before goes), so the
/// log never holds more than twice that.
/// <para>
/// Nothing in it is secret: callers give what kind of message failed, its size and which server sent it, never what it
/// said (a server's messages carry files and tokens). Each entry is clipped and has its control characters taken out,
/// so whatever an exception's message holds can't forge lines. Writing it never throws: a log that can't be written is
/// no log, and the app carries on.
/// </para>
/// </summary>
internal sealed class AppLog
{
    /// <summary>The most a log file holds before it's moved to .1.</summary>
    public const long MaxBytes = 1 << 20;

    /// <summary>The longest entry, in characters: longer is clipped.</summary>
    public const int MaxMessage = 400;

    private readonly object gate = new();
    private readonly long maxBytes;

    public AppLog(string folder, string kind, long maxBytes = MaxBytes)
    {
        Folder = folder;
        Kind = kind;
        this.maxBytes = maxBytes;
        FilePath = Path.Combine(folder, $"llamatree-{kind}.log");
    }

    /// <summary>The logs folder.</summary>
    public string Folder { get; }

    /// <summary>Which process writes it: "app" (a window), "host" (the session host) or "bridge" (on a server, over SSH).</summary>
    public string Kind { get; }

    public string FilePath { get; }

    /// <summary>This process's log, once <see cref="Start"/>ed; until then (in tests, say) nothing is written.</summary>
    public static AppLog? Current { get; internal set; }

    /// <summary>The logs folder in the app's local data (or a separate profile's folder, see AppProfile).</summary>
    public static string DefaultFolder => Path.Combine(AppProfile.CustomFolder ?? AppProfile.LocalFolder, "logs");

    /// <summary>Starts this process's log, as <paramref name="kind"/> (see <see cref="Kind"/>).</summary>
    public static void Start(string kind)
    {
        try { Current = new AppLog(DefaultFolder, kind); }
        catch (Exception) { /* no data folder (no HOME): no log */ }
    }

    public static void Info(string message) => Current?.Write("INFO", message);
    public static void Warn(string message) => Current?.Write("WARN", message);
    public static void Error(string message) => Current?.Write("ERROR", message);

    /// <summary>An exception as it's logged: its type and message, never its stack (or anything else it carries).</summary>
    public static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    /// <summary>
    /// What kind of message <paramref name="json"/> is (its "type", or a window op's "op"), for the log, never what it
    /// says: only letters, digits and . _ - are kept, and at most 40 of them. "?" when it doesn't say.
    /// </summary>
    public static string MessageType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "?";
            foreach (var name in (string[])["type", "op"])
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var safe = new string(v.GetString()!.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').Take(40).ToArray());
                    return safe.Length > 0 ? safe : "?";
                }
        }
        catch (Exception) { /* not JSON */ }
        return "?";
    }

    /// <summary>One entry, as the line written: control characters (line breaks, escapes) made spaces, clipped to <see cref="MaxMessage"/>.</summary>
    internal string Line(DateTime utc, string level, string message) =>
        $"{utc:yyyy-MM-ddTHH:mm:ss.fffZ} {Kind} {level} {Clean(message)}";

    internal static string Clean(string message)
    {
        var sb = new StringBuilder(Math.Min(message.Length, MaxMessage + 1));
        foreach (var c in message)
        {
            if (sb.Length >= MaxMessage)
            {
                sb.Append('…');
                break;
            }
            // Control characters, and the line and paragraph separators some viewers break lines at.
            sb.Append(char.IsControl(c) || c is (char)0x2028 or (char)0x2029 ? ' ' : c);
        }
        return sb.ToString();
    }

    /// <summary>Adds an entry. Never throws.</summary>
    public void Write(string level, string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Line(DateTime.UtcNow, level, message) + "\n");
            lock (gate)
            {
                CreateFolder(Folder);
                using var f = Open();
                if (f.Length > 0 && f.Length + bytes.Length > maxBytes)
                {
                    f.Dispose();
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                    using var fresh = Open();
                    fresh.Write(bytes);
                    return;
                }
                f.Write(bytes);
            }
        }
        catch (Exception) { /* a log that can't be written is no log: the app carries on */ }
    }

    /// <summary>Makes a logs folder if it isn't there, only its owner's on macOS and Linux (0700) as it's made. Returns it.</summary>
    internal static string CreateFolder(string folder)
    {
        if (Directory.Exists(folder)) return folder;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return folder;
    }

    /// <summary>The file, to add to: made only its owner's on macOS and Linux (0600) as it's made, never after.</summary>
    private FileStream Open()
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(FilePath, options);
    }
}
