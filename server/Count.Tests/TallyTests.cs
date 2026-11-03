namespace Llamachant.Count.Tests;

/// <summary>Adding up the rows: users per day, and each user once across the period.</summary>
public sealed class TallyTests
{
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static PingRow Row(int day, string id, string version = "1.2.0", string windows = "10.0.26300") =>
        new(Tally.Key(Monday.AddDays(day)), id, version, windows);

    [Fact]
    public void Each_day_counts_its_own_users_and_a_day_without_any_is_zero()
    {
        var t = Tally.Of([Row(0, "a"), Row(0, "b"), Row(2, "a")], Monday, Monday.AddDays(2));

        Assert.Equal(["2026-11-02", "2026-11-03", "2026-11-04"], t.Days.Select(d => d.Day));
        Assert.Equal([2, 0, 1], t.Days.Select(d => d.Users));
    }

    [Fact]
    public void Across_the_period_a_user_counts_once_under_the_version_they_ran_last()
    {
        var t = Tally.Of([Row(0, "a", "1.1.6"), Row(1, "a", "1.2.0"), Row(1, "b", "1.1.6")], Monday, Monday.AddDays(1));

        Assert.Equal(2, t.Users);
        Assert.Equal(new Dictionary<string, int> { ["1.1.6"] = 1, ["1.2.0"] = 1 }, t.Versions);
        Assert.Equal(new Dictionary<string, int> { ["1.1.6"] = 1 }, t.Days[0].Versions);
    }

    [Fact]
    public void Versions_are_listed_most_used_first()
    {
        var t = Tally.Of([Row(0, "a", "1.1.6"), Row(0, "b", "1.2.0"), Row(0, "c", "1.2.0")], Monday, Monday);
        Assert.Equal(["1.2.0", "1.1.6"], t.Versions.Keys);
    }

    [Fact]
    public void Windows_versions_are_counted_per_user()
    {
        var t = Tally.Of([Row(0, "a", windows: "10.0.26300"), Row(0, "b", windows: "10.0.19045"), Row(1, "b", windows: "10.0.19045")],
            Monday, Monday.AddDays(1));
        Assert.Equal(new Dictionary<string, int> { ["10.0.19045"] = 1, ["10.0.26300"] = 1 }, t.Windows);
    }

    [Fact]
    public void Rows_outside_the_period_are_left_out()
    {
        var t = Tally.Of([Row(-1, "old"), Row(0, "a"), Row(5, "later")], Monday, Monday);
        Assert.Equal(1, t.Users);
        Assert.Equal("2026-11-02", t.From);
        Assert.Equal("2026-11-02", t.To);
    }
}
