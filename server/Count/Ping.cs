using System.Globalization;
using System.Text.RegularExpressions;

namespace Llamachant.Count;

/// <summary>
/// One copy of the app saying it's open this hour: its random id (made when its user said yes), the app's version, the
/// operating system's name and version, and how many projects and sessions are open. Exactly what the app sends;
/// nothing else is kept, not even the address it came from. The counts are only numbers: the app never sends the
/// names of projects or sessions, or anything in them.
/// </summary>
internal sealed partial record Ping(string Id, string Version, string Os, string OsVersion, int Projects, int Sessions)
{
    /// <summary>More than anyone has open: a count above it isn't from the app.</summary>
    public const int MaxCount = 10_000;

    /// <summary>The ping in a request's query string (id, v, os, osv, projects, sessions), or null when any of them is missing or not what the app sends.</summary>
    public static Ping? Parse(string? id, string? version, string? os, string? osVersion, string? projects, string? sessions)
    {
        if (id == null || !IdPattern().IsMatch(id)) return null;
        if (version == null || !VersionPattern().IsMatch(version)) return null;
        if (os == null || !OsPattern().IsMatch(os)) return null;
        if (osVersion == null || !VersionPattern().IsMatch(osVersion)) return null;
        if (Count(projects) is not { } p || Count(sessions) is not { } s) return null;
        return new Ping(id.ToLowerInvariant(), version, os, osVersion, p, s);
    }

    private static int? Count(string? value) =>
        value is { Length: > 0 and <= 5 } && value.All(char.IsAsciiDigit) &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= MaxCount ? n : null;

    [GeneratedRegex("^[0-9a-fA-F]{32}$")]
    private static partial Regex IdPattern();

    // 1.2.0, 10.0.26200: up to four numbers, so nothing but a version can be stored under these names.
    [GeneratedRegex(@"^\d{1,5}(\.\d{1,5}){0,3}$")]
    private static partial Regex VersionPattern();

    // windows, macos, linux: a platform's name, in lower case.
    [GeneratedRegex("^[a-z]{1,16}$")]
    private static partial Regex OsPattern();
}
