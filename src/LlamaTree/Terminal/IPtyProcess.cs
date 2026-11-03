namespace LlamaTree.Terminal;

/// <summary>A program running in a pseudo terminal: ConPTY on Windows, a Unix pty on macOS and Linux.</summary>
internal interface IPtyProcess : IDisposable
{
    /// <summary>Decoded UTF-8 output, raised on a background thread.</summary>
    event Action<string>? Output;

    /// <summary>The exit code, raised on a background thread once the remaining output has been delivered.</summary>
    event Action<int>? Exited;

    int ProcessId { get; }

    /// <summary>Starts <paramref name="command"/>, a command as the user would type it (see <see cref="CommandLineResolver"/>).</summary>
    void Start(string command, string workingDirectory, IReadOnlyDictionary<string, string> environment, int cols, int rows);

    void Write(string text);

    void Resize(int cols, int rows);

    /// <summary>Every process still running in the terminal: the started process and everything it launched.</summary>
    int[] ProcessIds();
}

internal static class PtyProcess
{
    public static IPtyProcess Create() => OperatingSystem.IsWindows() ? new PseudoConsoleProcess() : new UnixPtyProcess();
}
