namespace LlamaTree.Tests;

/// <summary>A test of Windows behaviour (cmd, PATHEXT, drive letters, junctions...): skipped on macOS and Linux.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

/// <summary>A test of macOS and Linux behaviour: skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "macOS and Linux only";
    }
}

internal static class Platforms
{
    /// <summary>A session command for this system: cmd's on Windows, the shell's elsewhere.</summary>
    public static string Command(string windows, string unix) => OperatingSystem.IsWindows() ? windows : unix;
}
