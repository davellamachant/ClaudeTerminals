using System.Text.RegularExpressions;

namespace Llamachant.Count;

/// <summary>
/// One copy of the app saying it ran today: its random id (made when its user said yes), the app's version and the
/// Windows version. Exactly what the app sends; nothing else is kept, not even the address it came from.
/// </summary>
internal sealed partial record Ping(string Id, string Version, string Windows)
{
    /// <summary>The ping in a request's query string (id, v, os), or null when any of them is missing or not what the app sends.</summary>
    public static Ping? Parse(string? id, string? version, string? windows)
    {
        if (id == null || !IdPattern().IsMatch(id)) return null;
        if (version == null || !VersionPattern().IsMatch(version)) return null;
        if (windows == null || !VersionPattern().IsMatch(windows)) return null;
        return new Ping(id.ToLowerInvariant(), version, windows);
    }

    [GeneratedRegex("^[0-9a-fA-F]{32}$")]
    private static partial Regex IdPattern();

    // 1.2.0, 10.0.26200: up to four numbers, so nothing but a version can be stored under these names.
    [GeneratedRegex(@"^\d{1,5}(\.\d{1,5}){0,3}$")]
    private static partial Regex VersionPattern();
}
