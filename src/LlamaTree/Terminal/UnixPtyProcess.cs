using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace LlamaTree.Terminal;

/// <summary>
/// A child process in a Unix pseudo terminal (macOS, Linux). Output is raised as decoded UTF-8 text on a background
/// thread; disposing hangs up the terminal and kills what's left of the process tree.
/// <para>
/// The child is started with posix_spawn, so no code of this process runs in it (after a plain fork the .NET runtime
/// can't run in the child). It gets a session of its own with the terminal as its controlling terminal, which is what
/// makes resizing (SIGWINCH) and Ctrl+C reach the program: Linux makes the terminal the controlling one when the new
/// session opens it; macOS needs an explicit TIOCSCTTY, which a one-line perl script (perl comes with macOS) does
/// before running the command.
/// </para>
/// </summary>
internal sealed unsafe class UnixPtyProcess : IPtyProcess
{
    /// <summary>One start at a time: ptsname's answer is shared, and a new terminal must be close-on-exec before another start.</summary>
    private static readonly object SpawnLock = new();

    /// <summary>macOS: makes the terminal on standard input the controlling one, then runs the command.</summary>
    private const string MacControllingTerminal = "ioctl(STDIN, 0x20007461, 0); exec { $ARGV[0] } @ARGV; die \"$ARGV[0]: $!\\n\"";

    /// <summary>Guards <see cref="master"/>: it's closed (and set to -1) under this lock, so no write or resize uses a number since reused.</summary>
    private readonly object masterLock = new();
    private int master = -1;
    private int slave = -1;
    private int disposed;
    private Thread? reader;
    private Thread? writer;
    private readonly BlockingCollection<byte[]> writes = new();

    public event Action<string>? Output;
    public event Action<int>? Exited;

    public int ProcessId { get; private set; }

    public void Start(string command, string workingDirectory, IReadOnlyDictionary<string, string> environment, int cols, int rows, string? within = null)
    {
        // Confined: started in the folder as opened and checked, whatever is renamed meanwhile (see ConfinedFolder).
        using var confined = within != null ? ConfinedFolder.Open(workingDirectory, within) : null;
        var argv = CommandLineResolver.ResolveArgv(command, environment);
        if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/perl")) argv = ["/usr/bin/perl", "-e", MacControllingTerminal, "--", .. argv];
        var libc = Libc.Instance;
        var owned = new List<IntPtr>();
        var attr = NativeMemory.AllocZeroed(1024);              // posix_spawnattr_t: a pointer on macOS, 336 bytes with glibc
        var actions = NativeMemory.AllocZeroed(1024);           // posix_spawn_file_actions_t: likewise
        var signals = (byte*)NativeMemory.AllocZeroed(128);     // sigset_t: 4 bytes on macOS, 128 with glibc
        var noSignals = (byte*)NativeMemory.AllocZeroed(128);
        bool actionsMade = false, attrMade = false;
        try
        {
            lock (SpawnLock)
            {
                // The terminal: its controlling side for this process, and the program's side opened here too, so that
                // reading doesn't end (EIO) before the program has opened its own copy.
                master = libc.posix_openpt(Libc.O_RDWR | libc.O_NOCTTY);
                if (master < 0) throw Failed("posix_openpt");
                libc.Ioctl(master, libc.FIOCLEX, null);
                if (libc.grantpt(master) != 0 || libc.unlockpt(master) != 0) throw Failed("grantpt");
                var name = libc.ptsname(master);
                if (name == null) throw Failed("ptsname");
                var slavePath = Marshal.PtrToStringUTF8((IntPtr)name)!;
                slave = libc.open(Utf8(slavePath, owned), Libc.O_RDWR | libc.O_NOCTTY);
                if (slave < 0) throw Failed("open " + slavePath);
                libc.Ioctl(slave, libc.FIOCLEX, null);
                // The size is set once the program's side is open: macOS resets it when that side is first opened.
                var size = WinSize(cols, rows);
                libc.Ioctl(slave, libc.TIOCSWINSZ, &size);

                Check(libc.posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
                actionsMade = true;
                // Opened (not just duplicated) in the child, after setsid: on Linux that makes it the controlling terminal.
                Check(libc.posix_spawn_file_actions_addopen(actions, 0, Utf8(slavePath, owned), Libc.O_RDWR, 0), "addopen");
                Check(libc.posix_spawn_file_actions_adddup2(actions, 0, 1), "adddup2");
                Check(libc.posix_spawn_file_actions_adddup2(actions, 0, 2), "adddup2");
                if (confined != null)
                    Check(libc.posix_spawn_file_actions_addfchdir_np(actions, confined.Descriptor), "addfchdir");
                else if (libc.posix_spawn_file_actions_addchdir_np != null)
                    Check(libc.posix_spawn_file_actions_addchdir_np(actions, Utf8(workingDirectory, owned)), "addchdir");
                else argv = ["/bin/sh", "-c", "cd \"$0\" && exec \"$@\"", workingDirectory, .. argv];

                Check(libc.posix_spawnattr_init(attr), "posix_spawnattr_init");
                attrMade = true;
                // This process ignores SIGPIPE, and an ignored signal stays ignored in the child; blocked ones stay
                // blocked. The program starts with the defaults a terminal gives it.
                libc.sigemptyset(signals);
                foreach (var sig in new[] { 1, 2, 3, 13, 15, 17, 20, 21, 22, 25 }) libc.sigaddset(signals, OperatingSystem.IsMacOS() ? MacSignal(sig) : sig);
                libc.sigemptyset(noSignals);
                Check(libc.posix_spawnattr_setsigdefault(attr, signals), "setsigdefault");
                Check(libc.posix_spawnattr_setsigmask(attr, noSignals), "setsigmask");
                // SETSIGDEF | SETSIGMASK | SETSID, and on macOS CLOEXEC_DEFAULT (nothing but the terminal is inherited).
                var flags = OperatingSystem.IsMacOS() ? 0x04 | 0x08 | 0x0400 | 0x4000 : 0x04 | 0x08 | 0x80;
                Check(libc.posix_spawnattr_setflags(attr, (short)flags), "setflags");

                var path = Utf8(argv[0], owned);
                var args = Array(argv, owned);
                var env = Array(environment.Select(kv => kv.Key + "=" + kv.Value).ToArray(), owned);
                int pid;
                var error = libc.posix_spawn(&pid, path, actions, attr, args, env);
                if (error != 0) throw new IOException($"Couldn't start {argv[0]} (posix_spawn failed, errno {error}).");
                ProcessId = pid;
            }
        }
        catch
        {
            CloseSlave();
            var m = Interlocked.Exchange(ref master, -1);
            if (m >= 0) libc.close(m);
            throw;
        }
        finally
        {
            if (actionsMade) libc.posix_spawn_file_actions_destroy(actions);
            if (attrMade) libc.posix_spawnattr_destroy(attr);
            NativeMemory.Free(actions);
            NativeMemory.Free(attr);
            NativeMemory.Free(signals);
            NativeMemory.Free(noSignals);
            foreach (var p in owned) NativeMemory.Free((void*)p);
        }

        // The writer first: a program that ends at once ends the reader, which then waits for the writer, and waiting
        // for a thread that hasn't started yet throws (on a thread of its own, taking the app down).
        reader = new Thread(ReadLoop) { IsBackground = true, Name = $"pty-read-{ProcessId}" };
        writer = new Thread(WriteLoop) { IsBackground = true, Name = $"pty-write-{ProcessId}" };
        writer.Start();
        reader.Start();
        new Thread(WaitForExit) { IsBackground = true, Name = $"pty-exit-{ProcessId}" }.Start();
    }

    /// <summary>Linux's signal numbers as macOS's: SIGCHLD 17→20 and SIGTSTP 20→18; the others used here match.</summary>
    private static int MacSignal(int linux) => linux switch { 17 => 20, 20 => 18, _ => linux };

    private static IOException Failed(string what) => new($"Couldn't make a terminal ({what} failed, errno {Marshal.GetLastSystemError()}).");

    private static void Check(int result, string what)
    {
        if (result != 0) throw new IOException($"Couldn't start the terminal's program ({what} failed: {result}).");
    }

    /// <summary>This process's copy of the program's side: once it's closed, reading ends when the program's copies close.</summary>
    private void CloseSlave()
    {
        var s = Interlocked.Exchange(ref slave, -1);
        if (s >= 0) Libc.Instance.close(s);
    }

    public void Write(string text)
    {
        if (Volatile.Read(ref disposed) != 0 || master < 0) return;
        try { writes.Add(Encoding.UTF8.GetBytes(text)); }
        catch (InvalidOperationException) { /* the terminal is closing */ }
    }

    /// <summary>
    /// Writes on a thread of its own: a terminal's input buffer is small, and a big paste waits for the program to read
    /// it. The terminal stays open until this has finished (see <see cref="ReadLoop"/>).
    /// </summary>
    private void WriteLoop()
    {
        var fd = master;
        try
        {
            foreach (var bytes in writes.GetConsumingEnumerable())
            {
                fixed (byte* start = bytes)
                {
                    var done = 0;
                    while (done < bytes.Length)
                    {
                        var n = Libc.Write(fd, start + done, bytes.Length - done);
                        if (n > 0) { done += (int)n; continue; }
                        if (n < 0 && Marshal.GetLastPInvokeError() == 4 /* EINTR */) continue;
                        return;   // the terminal is gone
                    }
                }
            }
        }
        catch (ObjectDisposedException) { }
        finally
        {
            writes.CompleteAdding();   // nothing more is taken
        }
    }

    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0 || Volatile.Read(ref disposed) != 0) return;
        var size = WinSize(cols, rows);
        lock (masterLock)
            if (master >= 0) Libc.Instance.Ioctl(master, Libc.Instance.TIOCSWINSZ, &size);   // also sends the foreground programs SIGWINCH
    }

    private void ReadLoop()
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var buffer = new byte[64 * 1024];
        var chars = new char[64 * 1024 + 4];
        var fd = master;
        try
        {
            while (true)
            {
                nint read;
                fixed (byte* p = buffer) read = Libc.Read(fd, p, buffer.Length);
                if (read < 0 && Marshal.GetLastPInvokeError() == 4 /* EINTR */) continue;
                if (read <= 0) break;   // EIO once nothing has the terminal open any more
                var count = decoder.GetChars(buffer, 0, (int)read, chars, 0, false);
                if (count > 0) Output?.Invoke(new string(chars, 0, count));
            }
            var rest = decoder.GetChars([], 0, 0, chars, 0, true);   // an unfinished character at the very end
            if (rest > 0) Output?.Invoke(new string(chars, 0, rest));
        }
        finally
        {
            // Closed only here, once nothing reads or writes it: closed while still in use, its number could be reused by
            // a file opened meanwhile, which would then get the terminal's input or be read as its output.
            writes.CompleteAdding();
            if (writer?.Join(TimeSpan.FromSeconds(5)) != false)
                lock (masterLock)
                {
                    master = -1;
                    Libc.Instance.close(fd);
                }
        }
    }

    private void WaitForExit()
    {
        int status;
        int result;
        do result = Libc.WaitPid(ProcessId, &status, 0);
        while (result < 0 && Marshal.GetLastPInvokeError() == 4 /* EINTR */);
        var code = result < 0 ? -1 : (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
        CloseSlave();
        // Let the reader deliver the last output (an error message, say) first; bounded, as a program the session
        // started in the background can keep the terminal open.
        reader?.Join(TimeSpan.FromSeconds(5));
        Exited?.Invoke(code);
    }

    public int[] ProcessIds() => ProcessId > 0 && Volatile.Read(ref disposed) == 0 ? ProcessTree.Descendants(ProcessId) : [];

    /// <summary>Hangs up the terminal, as closing a terminal window does, then kills whatever is still running.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Output = null;
        Exited = null;
        writes.CompleteAdding();
        CloseSlave();
        var pid = ProcessId;
        if (pid <= 0) return;
        var tree = ProcessTree.Descendants(pid);
        var libc = Libc.Instance;
        libc.kill(-pid, 1 /* SIGHUP: the terminal's process group */);
        foreach (var p in tree) libc.kill(p, 1);
        Task.Delay(1500).ContinueWith(_ =>
        {
            libc.kill(-pid, 9 /* SIGKILL */);
            foreach (var p in tree) libc.kill(p, 9);
        }, TaskScheduler.Default);
    }

    private static Libc.WinSizeStruct WinSize(int cols, int rows) => new()
    {
        Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
        Cols = (ushort)Math.Clamp(cols, 2, ushort.MaxValue),
    };

    private static byte* Utf8(string s, List<IntPtr> owned)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(p, bytes.Length));
        p[bytes.Length] = 0;
        owned.Add((IntPtr)p);
        return p;
    }

    private static byte** Array(string[] items, List<IntPtr> owned)
    {
        var p = (byte**)NativeMemory.Alloc((nuint)((items.Length + 1) * sizeof(byte*)));
        owned.Add((IntPtr)p);
        for (var i = 0; i < items.Length; i++) p[i] = Utf8(items[i], owned);
        p[items.Length] = null;
        return p;
    }
}

/// <summary>The libc calls the Unix terminal (and <see cref="Platform.PathText"/>) need, looked up once.</summary>
internal sealed unsafe class Libc
{
    private static Libc? instance;
    public static Libc Instance => instance ??= new Libc();

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSizeStruct
    {
        public ushort Rows, Cols, XPixel, YPixel;
    }

    public const int O_RDWR = 2;
    public readonly int O_NOCTTY = OperatingSystem.IsMacOS() ? 0x20000 : 0x100;
    public readonly nuint TIOCSWINSZ = OperatingSystem.IsMacOS() ? 0x80087467u : 0x5414u;
    public readonly nuint FIOCLEX = OperatingSystem.IsMacOS() ? 0x20006601u : 0x5451u;

    public readonly delegate* unmanaged<int, int> posix_openpt;
    public readonly delegate* unmanaged<int, int> grantpt;
    public readonly delegate* unmanaged<int, int> unlockpt;
    public readonly delegate* unmanaged<int, byte*> ptsname;
    public readonly delegate* unmanaged<byte*, int, int> open;
    public readonly delegate* unmanaged<int*, byte*, void*, void*, byte**, byte**, int> posix_spawn;
    public readonly delegate* unmanaged<void*, int> posix_spawn_file_actions_init;
    public readonly delegate* unmanaged<void*, int> posix_spawn_file_actions_destroy;
    public readonly delegate* unmanaged<void*, int, byte*, int, int, int> posix_spawn_file_actions_addopen;
    public readonly delegate* unmanaged<void*, int, int, int> posix_spawn_file_actions_adddup2;
    /// <summary>glibc 2.29+, macOS 10.15+; null where it's missing.</summary>
    public readonly delegate* unmanaged<void*, byte*, int> posix_spawn_file_actions_addchdir_np;
    /// <summary>Starts the child in an open folder rather than at a path (glibc 2.29+, macOS 10.15+); null where it's missing.</summary>
    public readonly delegate* unmanaged<void*, int, int> posix_spawn_file_actions_addfchdir_np;
    public const int O_RDONLY = 0;
    public readonly int O_DIRECTORY = OperatingSystem.IsMacOS() ? 0x100000 : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    public readonly int O_CLOEXEC = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private readonly IntPtr fcntl;
    public readonly delegate* unmanaged<void*, int> posix_spawnattr_init;
    public readonly delegate* unmanaged<void*, int> posix_spawnattr_destroy;
    public readonly delegate* unmanaged<void*, short, int> posix_spawnattr_setflags;
    public readonly delegate* unmanaged<void*, byte*, int> posix_spawnattr_setsigdefault;
    public readonly delegate* unmanaged<void*, byte*, int> posix_spawnattr_setsigmask;
    public readonly delegate* unmanaged<byte*, int> sigemptyset;
    public readonly delegate* unmanaged<byte*, int, int> sigaddset;
    public readonly delegate* unmanaged<int, int> close;
    public readonly delegate* unmanaged<int, int, int> kill;
    private readonly IntPtr ioctl;
    private readonly delegate* unmanaged<byte*, byte*, byte*> realpath;
    private readonly delegate* unmanaged<void*, void> free;

    private static string LibraryPath => OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6";

    // The calls whose errno matters (EINTR) are imported with SetLastError, which saves errno straight after the call,
    // before the runtime's own work on the way back could change it. "libc" is resolved to the C library here.
    static Libc() => NativeLibrary.SetDllImportResolver(typeof(Libc).Assembly,
        (name, _, _) => name == "libc" ? NativeLibrary.Load(LibraryPath) : IntPtr.Zero);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    public static extern nint Read(int fd, byte* buffer, nint count);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    public static extern nint Write(int fd, byte* buffer, nint count);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    public static extern int WaitPid(int pid, int* status, int options);

    private Libc()
    {
        var library = NativeLibrary.Load(LibraryPath);
        IntPtr Find(string name) => NativeLibrary.TryGetExport(library, name, out var f) ? f
            : throw new DllNotFoundException($"'{name}' wasn't found in the C library.");
        posix_openpt = (delegate* unmanaged<int, int>)Find("posix_openpt");
        grantpt = (delegate* unmanaged<int, int>)Find("grantpt");
        unlockpt = (delegate* unmanaged<int, int>)Find("unlockpt");
        ptsname = (delegate* unmanaged<int, byte*>)Find("ptsname");
        open = (delegate* unmanaged<byte*, int, int>)Find("open");
        posix_spawn = (delegate* unmanaged<int*, byte*, void*, void*, byte**, byte**, int>)Find("posix_spawn");
        posix_spawn_file_actions_init = (delegate* unmanaged<void*, int>)Find("posix_spawn_file_actions_init");
        posix_spawn_file_actions_destroy = (delegate* unmanaged<void*, int>)Find("posix_spawn_file_actions_destroy");
        posix_spawn_file_actions_addopen = (delegate* unmanaged<void*, int, byte*, int, int, int>)Find("posix_spawn_file_actions_addopen");
        posix_spawn_file_actions_adddup2 = (delegate* unmanaged<void*, int, int, int>)Find("posix_spawn_file_actions_adddup2");
        posix_spawn_file_actions_addchdir_np = NativeLibrary.TryGetExport(library, "posix_spawn_file_actions_addchdir_np", out var chdir)
            ? (delegate* unmanaged<void*, byte*, int>)chdir : null;
        posix_spawnattr_init = (delegate* unmanaged<void*, int>)Find("posix_spawnattr_init");
        posix_spawnattr_destroy = (delegate* unmanaged<void*, int>)Find("posix_spawnattr_destroy");
        posix_spawnattr_setflags = (delegate* unmanaged<void*, short, int>)Find("posix_spawnattr_setflags");
        posix_spawnattr_setsigdefault = (delegate* unmanaged<void*, byte*, int>)Find("posix_spawnattr_setsigdefault");
        posix_spawnattr_setsigmask = (delegate* unmanaged<void*, byte*, int>)Find("posix_spawnattr_setsigmask");
        sigemptyset = (delegate* unmanaged<byte*, int>)Find("sigemptyset");
        sigaddset = (delegate* unmanaged<byte*, int, int>)Find("sigaddset");
        close = (delegate* unmanaged<int, int>)Find("close");
        kill = (delegate* unmanaged<int, int, int>)Find("kill");
        ioctl = Find("ioctl");
        fcntl = Find("fcntl");
        posix_spawn_file_actions_addfchdir_np = NativeLibrary.TryGetExport(library, "posix_spawn_file_actions_addfchdir_np", out var fchdir)
            ? (delegate* unmanaged<void*, int, int>)fchdir : null;
        realpath = (delegate* unmanaged<byte*, byte*, byte*>)Find("realpath");
        free = (delegate* unmanaged<void*, void>)Find("free");
    }

    /// <summary>realpath(3): the path with every link resolved, or null if it doesn't exist.</summary>
    public string? RealPath(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        byte* result;
        fixed (byte* p = bytes) result = realpath(p, null);
        if (result == null) return null;
        try { return Marshal.PtrToStringUTF8((IntPtr)result); }
        finally { free(result); }
    }

    /// <summary>
    /// ioctl(fd, request, arg). It's variadic: on Apple's arm64 a variadic argument goes on the stack rather than in a
    /// register, so there it's passed as the ninth argument (the first past the eight argument registers).
    /// </summary>
    /// <summary>
    /// The path of the folder or file <paramref name="fd"/> has open, as the system knows it now: wherever it has been moved
    /// since. Linux reads /proc/self/fd; macOS asks fcntl(F_GETPATH). Null if it can't be told.
    /// </summary>
    public string? PathOf(int fd)
    {
        if (!OperatingSystem.IsMacOS())
        {
            try { return new FileInfo($"/proc/self/fd/{fd}").LinkTarget; }
            catch (IOException) { return null; }
        }
        const int F_GETPATH = 50;
        var buffer = stackalloc byte[1024];   // MAXPATHLEN
        var r = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ((delegate* unmanaged<int, int, nint, nint, nint, nint, nint, nint, byte*, int>)fcntl)(fd, F_GETPATH, 0, 0, 0, 0, 0, 0, buffer)   // variadic: see Ioctl
            : ((delegate* unmanaged<int, int, byte*, int>)fcntl)(fd, F_GETPATH, buffer);
        return r == -1 ? null : Marshal.PtrToStringUTF8((IntPtr)buffer);
    }

    public int Ioctl(int fd, nuint request, void* arg)
    {
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return ((delegate* unmanaged<int, nuint, nint, nint, nint, nint, nint, nint, void*, int>)ioctl)(fd, request, 0, 0, 0, 0, 0, 0, arg);
        return ((delegate* unmanaged<int, nuint, void*, int>)ioctl)(fd, request, arg);
    }
}

/// <summary>A process and everything it started, found from each process's parent.</summary>
internal static class ProcessTree
{
    private static readonly object SnapshotLock = new();
    private static (List<(int Pid, int Parent)> Table, long At) snapshot = ([], 0);

    public static int[] Descendants(int root)
    {
        // One look at the process table serves every session asking within a second or so (on macOS each look runs ps).
        List<(int Pid, int Parent)> parents;
        lock (SnapshotLock)
        {
            if (Environment.TickCount64 - snapshot.At > 1500 || snapshot.At == 0)
                snapshot = (OperatingSystem.IsLinux() ? FromProc() : FromPs(), Environment.TickCount64);
            parents = snapshot.Table;
        }
        var children = parents.GroupBy(x => x.Parent).ToDictionary(g => g.Key, g => g.Select(x => x.Pid).ToList());
        var found = new List<int> { root };
        for (var i = 0; i < found.Count && found.Count < 512; i++)
            if (children.TryGetValue(found[i], out var kids)) found.AddRange(kids.Where(k => !found.Contains(k)));
        return parents.Any(x => x.Pid == root) ? found.ToArray() : [];
    }

    /// <summary>Linux: field 4 of /proc/&lt;pid&gt;/stat, after the command name (which can hold spaces and parentheses).</summary>
    private static List<(int Pid, int Parent)> FromProc()
    {
        var list = new List<(int, int)>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (rest.Length > 1 && int.TryParse(rest[1], out var parent)) list.Add((pid, parent));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* it exited meanwhile */ }
        }
        return list;
    }

    /// <summary>macOS: ps, which every Mac has.</summary>
    private static List<(int Pid, int Parent)> FromPs()
    {
        var list = new List<(int, int)>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/ps", "-A -o pid= -o ppid=") { RedirectStandardOutput = true, UseShellExecute = false };
            using var ps = System.Diagnostics.Process.Start(psi);
            if (ps == null) return list;
            var text = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5000);
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var parent)) list.Add((pid, parent));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
        return list;
    }
}
