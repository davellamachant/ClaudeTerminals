namespace Llamachant.Count;

/// <summary>A row of the pings table: one copy (RowKey, its id) on one UTC day (PartitionKey, yyyy-MM-dd).</summary>
internal sealed record PingRow(string Day, string Id, string Version, string Windows);

/// <summary>Users per day, and in the whole period, with the versions they ran.</summary>
internal sealed record Tally(
    string From,
    string To,
    int Users,
    IReadOnlyList<DayTally> Days,
    IReadOnlyDictionary<string, int> Versions,
    IReadOnlyDictionary<string, int> Windows)
{
    /// <summary>
    /// Adds up the rows from <paramref name="from"/> to <paramref name="to"/>, every day listed (a day nobody ran it is a
    /// 0). In the whole period a user counts once, under the version they ran last.
    /// </summary>
    public static Tally Of(IEnumerable<PingRow> rows, DateOnly from, DateOnly to)
    {
        var inRange = rows.Where(r => DateOnly.TryParse(r.Day, out var d) && d >= from && d <= to).ToList();
        var byDay = inRange.GroupBy(r => r.Day).ToDictionary(g => g.Key, g => g.ToList());

        var days = new List<DayTally>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var key = Key(d);
            var list = byDay.GetValueOrDefault(key) ?? [];
            days.Add(new DayTally(key, list.Count, CountBy(list, r => r.Version)));
        }

        var latest = inRange.GroupBy(r => r.Id).Select(g => g.MaxBy(r => r.Day)!).ToList();
        return new Tally(Key(from), Key(to), latest.Count, days, CountBy(latest, r => r.Version), CountBy(latest, r => r.Windows));
    }

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd");

    private static IReadOnlyDictionary<string, int> CountBy(IEnumerable<PingRow> rows, Func<PingRow, string> by) =>
        rows.GroupBy(by).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());
}

internal sealed record DayTally(string Day, int Users, IReadOnlyDictionary<string, int> Versions);
