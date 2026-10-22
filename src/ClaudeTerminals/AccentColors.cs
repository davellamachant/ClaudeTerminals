using Microsoft.Win32;

namespace ClaudeTerminals;

/// <summary>
/// The Windows accent colour and its shades, as Settings › Personalisation › Colours sets them. The page picks which
/// shades to use (it knows its own background and text colours).
/// </summary>
internal static class AccentColors
{
    /// <summary>
    /// Seven shades as #rrggbb, lightest first: three lighter, the accent itself (index 3), three darker. Null when
    /// Windows doesn't say (the page then keeps its own colours).
    /// </summary>
    public static string[]? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            // 8 RGBA entries; the last isn't a shade of the accent.
            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 28)
                return Enumerable.Range(0, 7).Select(i => Hex(palette[i * 4], palette[i * 4 + 1], palette[i * 4 + 2])).ToArray();

            // Older Windows: only the accent itself (ABGR), so every shade is that colour.
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is int abgr)
            {
                var accent = Hex((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
                return Enumerable.Repeat(accent, 7).ToArray();
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return null;
    }

    /// <summary>Windows' "Choose your app mode": true for Light, false for Dark, null when it isn't set.</summary>
    public static bool? AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v != 0 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string Hex(byte r, byte g, byte b) => $"#{r:x2}{g:x2}{b:x2}";
}
