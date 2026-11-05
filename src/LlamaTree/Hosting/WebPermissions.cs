namespace LlamaTree.Hosting;

/// <summary>What the Windows window answers a page's request for a browser permission (see MainForm).</summary>
internal enum WebPermissionAnswer
{
    /// <summary>The web view's own default: it asks you.</summary>
    Default,
    Allow,
    Deny,
}

/// <summary>
/// Which browser permissions the Windows window's web view gives. The app page gets clipboard reads without a prompt
/// (the terminal's Paste menu item reads the clipboard; WebView2 would otherwise ask "Allow clipboard access?" the first
/// time, a prompt that also takes focus away from the terminal). Nothing else does: a frame in the app page is a
/// plugin's, and no plugin grant covers the clipboard (or a camera, location or notifications), so a plugin gets none of
/// them, and no prompt asking for them; nor does any other origin.
/// </summary>
internal static class WebPermissions
{
    /// <summary>The app page's virtual host (see MainForm).</summary>
    public const string AppHost = "app.local";

    /// <param name="clipboardRead">Whether it's for reading the clipboard.</param>
    /// <param name="uri">The origin asking, as the web view names it.</param>
    /// <param name="fromFrame">Whether a frame in the page is asking (a plugin's), rather than the page.</param>
    public static WebPermissionAnswer Decide(bool clipboardRead, string? uri, bool fromFrame)
    {
        if (fromFrame || !IsAppOrigin(uri)) return WebPermissionAnswer.Deny;
        return clipboardRead ? WebPermissionAnswer.Allow : WebPermissionAnswer.Default;
    }

    private static bool IsAppOrigin(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps &&
        string.Equals(origin.Host, AppHost, StringComparison.OrdinalIgnoreCase) && origin.IsDefaultPort;
}
