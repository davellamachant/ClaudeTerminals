using System.Runtime.InteropServices;
using System.Text;

namespace LlamaTree.Platform;

/// <summary>
/// Extended attributes on macOS and Linux (setxattr / getxattr), never following a symbolic link. Looked up from the C
/// library as it's first needed, so a system without them (or Windows) just gets false back. Best effort throughout:
/// nothing here throws.
/// </summary>
internal static unsafe class ExtendedAttributes
{
    private const int XATTR_NOFOLLOW = 0x0001;   // macOS: act on a link itself, not what it points to

    private static readonly IntPtr Library =
        OperatingSystem.IsWindows() ? IntPtr.Zero
        : NativeLibrary.TryLoad(OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6", out var lib) ? lib : IntPtr.Zero;

    // macOS: setxattr(path, name, value, size, position, options) and getxattr(path, name, value, size, position, options).
    // Linux: lsetxattr(path, name, value, size, flags) and lgetxattr(path, name, value, size), the l- forms not following links.
    private static readonly IntPtr Set = Export(OperatingSystem.IsMacOS() ? "setxattr" : "lsetxattr");
    private static readonly IntPtr Get = Export(OperatingSystem.IsMacOS() ? "getxattr" : "lgetxattr");

    private static IntPtr Export(string name) =>
        Library != IntPtr.Zero && NativeLibrary.TryGetExport(Library, name, out var f) ? f : IntPtr.Zero;

    /// <summary>Sets attribute <paramref name="name"/> of the file to <paramref name="value"/> (UTF-8, no terminating NUL). False where it can't be.</summary>
    public static bool TrySet(string path, string name, string value)
    {
        if (Set == IntPtr.Zero) return false;
        var p = Encoding.UTF8.GetBytes(path + "\0");
        var n = Encoding.UTF8.GetBytes(name + "\0");
        var v = Encoding.UTF8.GetBytes(value);
        fixed (byte* pp = p, pn = n, pv = v)
        {
            return OperatingSystem.IsMacOS()
                ? ((delegate* unmanaged<byte*, byte*, byte*, nuint, uint, int, int>)Set)(pp, pn, pv, (nuint)v.Length, 0, XATTR_NOFOLLOW) == 0
                : ((delegate* unmanaged<byte*, byte*, byte*, nuint, int, int>)Set)(pp, pn, pv, (nuint)v.Length, 0) == 0;
        }
    }

    /// <summary>Attribute <paramref name="name"/> of the file (as UTF-8), or null when it has none or can't be read.</summary>
    public static string? TryGet(string path, string name)
    {
        if (Get == IntPtr.Zero) return null;
        var p = Encoding.UTF8.GetBytes(path + "\0");
        var n = Encoding.UTF8.GetBytes(name + "\0");
        var buffer = new byte[4096];
        nint read;
        fixed (byte* pp = p, pn = n, pb = buffer)
        {
            read = OperatingSystem.IsMacOS()
                ? ((delegate* unmanaged<byte*, byte*, byte*, nuint, uint, int, nint>)Get)(pp, pn, pb, (nuint)buffer.Length, 0, XATTR_NOFOLLOW)
                : ((delegate* unmanaged<byte*, byte*, byte*, nuint, nint>)Get)(pp, pn, pb, (nuint)buffer.Length);
        }
        return read < 0 ? null : Encoding.UTF8.GetString(buffer, 0, (int)read);
    }
}
