using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static ClaudeTerminals.Terminal.Native;

namespace ClaudeTerminals.Terminal;

/// <summary>
/// A child process attached to a Windows pseudo console (ConPTY). Output is raised as decoded
/// UTF-8 text on a background thread; the process tree is bound to a job object so it dies with us.
/// </summary>
internal sealed class PseudoConsoleProcess : IDisposable
{
    private readonly object writeLock = new();
    private IntPtr hPC;
    private IntPtr hProcess;
    private IntPtr hJob;
    private FileStream? input;
    private FileStream? output;
    private RegisteredWaitHandle? exitWait;
    private ManualResetEvent? exitEvent;
    private Thread? reader;
    private int disposed;

    public event Action<string>? Output;
    public event Action<int>? Exited;

    public int ProcessId { get; private set; }

    public void Start(string commandLine, string workingDirectory, string environmentBlock, int cols, int rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");

        var hr = CreatePseudoConsole(Size(cols, rows), inputRead, outputWrite, 0, out hPC);
        if (hr != 0)
            throw new Win32Exception(hr, "CreatePseudoConsole failed");

        // The pseudo console duplicates these; our copies must be closed so EOF propagates.
        inputRead.Dispose();
        outputWrite.Dispose();

        input = new FileStream(inputWrite, FileAccess.Write, 1);
        output = new FileStream(outputRead, FileAccess.Read, 1);

        var attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        var attrList = Marshal.AllocHGlobal(attrSize);
        var envBlock = Marshal.StringToHGlobalUni(environmentBlock);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var si = new STARTUPINFOEX { lpAttributeList = attrList };
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            // Explicitly no standard handles: otherwise a child started while this app's own output is redirected (from a
            // script, a test runner) writes to that redirection instead of the pseudo console, and nothing shows.
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;

            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                    envBlock, workingDirectory, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to start: {commandLine}");

            hProcess = pi.hProcess;
            ProcessId = pi.dwProcessId;
            hJob = CreateKillOnCloseJob();
            if (hJob != IntPtr.Zero)
                AssignProcessToJobObject(hJob, hProcess);
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            Marshal.FreeHGlobal(envBlock);
        }

        reader = new Thread(ReadLoop) { IsBackground = true, Name = $"pty-read-{ProcessId}" };
        reader.Start();

        exitEvent = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(hProcess, false) };
        // The exit handler waits for the reader to drain, so it gets its own thread rather than holding a thread-pool
        // thread (shared with every async git call) while it waits.
        exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent,
            (_, _) => new Thread(OnProcessExited) { IsBackground = true, Name = $"pty-exit-{ProcessId}" }.Start(), null, -1, true);
    }

    public void Write(string text)
    {
        var stream = input;
        if (stream == null) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (writeLock)
        {
            try
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Resize(int cols, int rows)
    {
        if (hPC != IntPtr.Zero && cols > 0 && rows > 0)
            ResizePseudoConsole(hPC, Size(cols, rows));
    }

    private void ReadLoop()
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var buffer = new byte[64 * 1024];
        var chars = new char[64 * 1024 + 4];
        try
        {
            while (true)
            {
                var read = output!.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                var count = decoder.GetChars(buffer, 0, read, chars, 0, false);
                if (count > 0) Output?.Invoke(new string(chars, 0, count));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void OnProcessExited()
    {
        var code = GetExitCodeProcess(hProcess, out var c) ? unchecked((int)c) : -1;
        // Closing the pseudo console flushes the remaining output and lets the reader hit EOF.
        // It can block until output is drained, so never call it on the UI thread.
        ClosePseudoConsoleOnce();
        // Let the reader deliver that last output before reporting the exit: whoever handles Exited disposes this
        // object, which would cut off a final error message (on Windows 11 24H2 ClosePseudoConsole no longer waits).
        // Bounded so a pipe that never closes can't stop the exit from ever being reported; draining a final screen
        // takes milliseconds, so output is only lost if the reader is stuck for this long. Kept short because the
        // session shows no exit notice (and can't be restarted with Enter) until this returns.
        reader?.Join(TimeSpan.FromSeconds(5));
        Exited?.Invoke(code);
    }

    private void ClosePseudoConsoleOnce()
    {
        var pc = Interlocked.Exchange(ref hPC, IntPtr.Zero);
        if (pc != IntPtr.Zero) ClosePseudoConsole(pc);
    }

    /// <summary>Every process still running in this console's job: the started process and everything it launched.</summary>
    public int[] ProcessIds()
    {
        var job = hJob;
        if (job == IntPtr.Zero) return [];
        // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counts, then ULONG_PTR ids. Room for 256 processes is plenty.
        var buffer = new byte[8 + 256 * IntPtr.Size];
        if (!QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, buffer.Length, out _)) return [];
        var count = Math.Min(BitConverter.ToInt32(buffer, 4), 256);
        var ids = new int[count];
        for (var i = 0; i < count; i++)
            ids[i] = (int)(IntPtr.Size == 8 ? BitConverter.ToInt64(buffer, 8 + i * 8) : BitConverter.ToInt32(buffer, 8 + i * 4));
        return ids;
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        return job;
    }

    private static COORD Size(int cols, int rows) => new()
    {
        X = (short)Math.Clamp(cols, 2, short.MaxValue),
        Y = (short)Math.Clamp(rows, 1, short.MaxValue),
    };

    /// <summary>Kills the whole process tree and releases all handles.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Output = null;
        Exited = null;
        exitWait?.Unregister(null);
        if (hJob != IntPtr.Zero)
        {
            TerminateJobObject(hJob, 1);
            CloseHandle(hJob);
            hJob = IntPtr.Zero;
        }
        // ClosePseudoConsole may block until the output pipe is drained; do it off-thread.
        var stream = output;
        Task.Run(() =>
        {
            ClosePseudoConsoleOnce();
            stream?.Dispose();
        });
        input?.Dispose();
        exitEvent?.Dispose();
        if (hProcess != IntPtr.Zero)
        {
            CloseHandle(hProcess);
            hProcess = IntPtr.Zero;
        }
    }
}
