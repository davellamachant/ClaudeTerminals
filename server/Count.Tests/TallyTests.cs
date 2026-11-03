namespace Llamachant.Count.Tests;

/// <summary>Adding up the rows: users per day and hour, each user once across a day or the period, and what they had open.</summary>
public sealed class TallyTests
{
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static PingRow Row(int day, int hour, string id, string version = "1.2.0", string os = "windows", string osVersion = "10.0.26300",
        int projects = 1, int sessions = 2) => new(Tally.HourKey(Monday.AddDays(day), hour), id, version, os, osVersion, projects, sessions);

    [Fact]
    public void Each_day_counts_its_users_once_however_many_hours_they_were_open()
    {
        var t = Tally.Of([Row(0, 9, "a"), Row(0, 10, "a"), Row(0, 11, "a"), Row(0, 9, "b"), Row(2, 14, "a")], Monday, Monday.AddDays(2));

        Assert.Equal(["2026-11-02", "2026-11-03", "2026-11-04"], t.Days.Select(d => d.Day));
        Assert.Equal([2, 0, 1], t.Days.Select(d => d.Users));
        Assert.Equal([4, 0, 1], t.Days.Select(d => d.UserHours));
        Assert.Equal(2, t.Users);
    }

    [Fact]
    public void A_days_averages_are_of_the_most_each_user_had_open_at_once()
    {
        // a: up to 3 projects and 8 sessions that day; b: 1 and 2.
        var t = Tally.Of([Row(0, 9, "a", projects: 2, sessions: 4), Row(0, 10, "a", projects: 3, sessions: 8), Row(0, 9, "b", projects: 1, sessions: 2)],
            Monday, Monday);
        Assert.Equal(2.0, t.Days[0].AverageProjects);
        Assert.Equal(5.0, t.Days[0].AverageSessions);
    }

    [Fact]
    public void An_empty_day_averages_nothing() =>
        Assert.Equal((0.0, 0.0), Tally.Of([], Monday, Monday).Days[0] is var d ? (d.AverageProjects, d.AverageSessions) : default);

    [Fact]
    public void Each_hour_has_its_users_and_what_they_had_open_between_them()
    {
        var t = Tally.Of([Row(0, 9, "a", projects: 2, sessions: 4), Row(0, 9, "b", projects: 1, sessions: 3)], Monday, Monday);

        Assert.Equal(24, t.Hours!.Count);
        var nine = t.Hours.Single(h => h.Hour == "2026-11-02T09");
        Assert.Equal((2, 3, 7), (nine.Users, nine.Projects, nine.Sessions));
        Assert.Equal(0, t.Hours.Single(h => h.Hour == "2026-11-02T10").Users);
    }

    [Fact]
    public void Hours_are_listed_for_a_week_at_most()
    {
        Assert.Equal(7 * 24, Tally.Of([], Monday, Monday.AddDays(6)).Hours!.Count);
        Assert.Null(Tally.Of([], Monday, Monday.AddDays(7)).Hours);
    }

    [Fact]
    public void Across_the_period_a_user_counts_once_as_last_seen()
    {
        var t = Tally.Of([Row(0, 9, "a", "1.1.6"), Row(1, 9, "a", "1.2.0"), Row(1, 9, "b", "1.1.6", "macos", "15.1")], Monday, Monday.AddDays(1));

        Assert.Equal(2, t.Users);
        Assert.Equal(new Dictionary<string, int> { ["1.1.6"] = 1, ["1.2.0"] = 1 }, t.Versions);
        Assert.Equal(new Dictionary<string, int> { ["macos"] = 1, ["windows"] = 1 }, t.Systems);
        Assert.Equal(new Dictionary<string, int> { ["macos 15.1"] = 1, ["windows 10.0.26300"] = 1 }, t.SystemVersions);
        Assert.Equal(new Dictionary<string, int> { ["1.1.6"] = 1 }, t.Days[0].Versions);
    }

    [Fact]
    public void Versions_are_listed_most_used_first()
    {
        var t = Tally.Of([Row(0, 9, "a", "1.1.6"), Row(0, 9, "b", "1.2.0"), Row(0, 9, "c", "1.2.0")], Monday, Monday);
        Assert.Equal(["1.2.0", "1.1.6"], t.Versions.Keys);
    }

    [Fact]
    public void Rows_outside_the_period_are_left_out()
    {
        var t = Tally.Of([Row(-1, 23, "old"), Row(0, 0, "a"), Row(1, 0, "later")], Monday, Monday);
        Assert.Equal(1, t.Users);
        Assert.Equal("2026-11-02", t.From);
        Assert.Equal("2026-11-02", t.To);
    }
}
