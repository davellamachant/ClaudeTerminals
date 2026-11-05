using System.Globalization;

namespace Llamachant.Count;

/// <summary>A row of the pings table: one copy (RowKey, its id) in one UTC hour (PartitionKey, yyyy-MM-ddTHH).</summary>
internal sealed record PingRow(string Hour, string Id, string Version, string Os, string OsVersion, int Projects, int Sessions)
{
    public string Day => Hour[..10];
}

/// <summary>
/// The count for a period: users in it and per day (and per hour for a week or less), how much they had open, and the
/// versions and systems they ran.
/// </summary>
internal sealed record Tally(
    string From,
    string To,
    int Users,
    IReadOnlyList<DayTally> Days,
    IReadOnlyList<HourTally>? Hours,
    IReadOnlyDictionary<string, int> Versions,
    IReadOnlyDictionary<string, int> Systems,
    IReadOnlyDictionary<string, int> SystemVersions)
{
    /// <summary>Hours are listed for a period up to this long; longer, only days.</summary>
    public const int HoursUpToDays = 7;

    /// <summary>
    /// Adds up the rows from <paramref name="from"/> to <paramref name="to"/>, every day (and hour) listed, a day nobody
    /// had it open being 0. Across a day or the period a user counts once, as they were last seen.
    /// </summary>
    public static Tally Of(IEnumerable<PingRow> rows, DateOnly from, DateOnly to)
    {
        var inRange = rows.Where(r => DateOnly.TryParseExact(r.Day, "yyyy-MM-dd", out var d) && d >= from && d <= to).ToList();
        var byDay = inRange.GroupBy(r => r.Day).ToDictionary(g => g.Key, g => g.ToList());

        var days = new List<DayTally>();
        for (var d = from; d <= to; d = d.AddDays(1))
            days.Add(DayTally.Of(DayKey(d), byDay.GetValueOrDefault(DayKey(d)) ?? []));

        List<HourTally>? hours = null;
        if (to.DayNumber - from.DayNumber < HoursUpToDays)
        {
            var byHour = inRange.GroupBy(r => r.Hour).ToDictionary(g => g.Key, g => g.ToList());
            hours = [];
            for (var d = from; d <= to; d = d.AddDays(1))
                for (var h = 0; h < 24; h++)
                {
                    var key = HourKey(d, h);
                    var list = byHour.GetValueOrDefault(key) ?? [];
                    hours.Add(new HourTally(key, list.Count, list.Sum(r => r.Projects), list.Sum(r => r.Sessions)));
                }
        }

        var latest = Latest(inRange);
        return new Tally(DayKey(from), DayKey(to), latest.Count, days, hours,
            CountBy(latest, r => r.Version), CountBy(latest, r => r.Os), CountBy(latest, r => $"{r.Os} {r.OsVersion}"));
    }

    /// <summary>Each user's last row.</summary>
    internal static List<PingRow> Latest(IEnumerable<PingRow> rows) =>
        rows.GroupBy(r => r.Id).Select(g => g.MaxBy(r => r.Hour, StringComparer.Ordinal)!).ToList();

    public static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string HourKey(DateOnly day, int hour) => $"{DayKey(day)}T{hour:00}";

    public static string HourKey(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);

    internal static IReadOnlyDictionary<string, int> CountBy(IEnumerable<PingRow> rows, Func<PingRow, string> by) =>
        rows.GroupBy(by).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>
/// A day: its users; the hours they had it open (a user open three hours is three); and, per user, the most projects
/// and sessions they had open at once that day, averaged.
/// </summary>
internal sealed record DayTally(string Day, int Users, int UserHours, double AverageProjects, double AverageSessions, IReadOnlyDictionary<string, int> Versions)
{
    public static DayTally Of(string day, IReadOnlyList<PingRow> rows)
    {
        var users = rows.GroupBy(r => r.Id).ToList();
        double Average(Func<PingRow, int> by) => users.Count == 0 ? 0 : Math.Round(users.Average(u => u.Max(by)), 1);
        return new DayTally(day, users.Count, rows.Count, Average(r => r.Projects), Average(r => r.Sessions),
            Tally.CountBy(Tally.Latest(rows), r => r.Version));
    }
}

/// <summary>An hour: the users who had it open, and the projects and sessions open between them.</summary>
internal sealed record HourTally(string Hour, int Users, int Projects, int Sessions);
