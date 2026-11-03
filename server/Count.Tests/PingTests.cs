namespace Llamachant.Count.Tests;

/// <summary>Only what the app sends is taken: a 32 hex id, two version numbers, a system's name and two counts.</summary>
public sealed class PingTests
{
    private const string Id = "1868c4d72ca345de80adb48806e14052";

    private static Ping? Parse(string? id = Id, string? v = "1.2.0", string? os = "windows", string? osv = "10.0.26300",
        string? projects = "3", string? sessions = "7") => Ping.Parse(id, v, os, osv, projects, sessions);

    [Fact]
    public void What_the_app_sends_is_a_ping() =>
        Assert.Equal(new Ping(Id, "1.2.0", "windows", "10.0.26300", 3, 7), Parse());

    [Fact]
    public void The_id_is_kept_in_lower_case_so_one_copy_is_one_row() =>
        Assert.Equal(Id, Parse(id: Id.ToUpperInvariant())?.Id);

    [Fact]
    public void Other_platforms_are_taken_too() =>
        Assert.Equal("macos", Parse(os: "macos", osv: "15.1")?.Os);

    [Fact]
    public void Nothing_open_is_a_count_too() =>
        Assert.Equal((0, 0), Parse(projects: "0", sessions: "0") is { } p ? (p.Projects, p.Sessions) : default);

    [Theory]
    [InlineData(null, "1.2.0", "windows", "10.0.26300", "3", "7")]
    [InlineData("1868c4d72ca345de80adb48806e1405", "1.2.0", "windows", "10.0.26300", "3", "7")]     // 31 characters
    [InlineData("1868c4d72ca345de80adb48806e1405z", "1.2.0", "windows", "10.0.26300", "3", "7")]    // not hex
    [InlineData(Id, "1.2.0-beta", "windows", "10.0.26300", "3", "7")]
    [InlineData(Id, "1.2.0.0.0", "windows", "10.0.26300", "3", "7")]                                // five numbers
    [InlineData(Id, "1.2.0", "Windows 11", "10.0.26300", "3", "7")]                                 // not a name
    [InlineData(Id, "1.2.0", "dave@example.com", "10.0.26300", "3", "7")]
    [InlineData(Id, "1.2.0", "windows", "dave@example.com", "3", "7")]
    [InlineData(Id, "1.2.0", "windows", null, "3", "7")]
    [InlineData(Id, "1.2.0", "windows", "10.0.26300", "My project", "7")]                           // a name, not a count
    [InlineData(Id, "1.2.0", "windows", "10.0.26300", "3", "-1")]
    [InlineData(Id, "1.2.0", "windows", "10.0.26300", "3", "10001")]                                // more than anyone has
    [InlineData(Id, "1.2.0", "windows", "10.0.26300", "3", "")]
    [InlineData(Id, "1.2.0", "windows", "10.0.26300", null, "7")]
    public void Anything_else_is_refused(string? id, string? v, string? os, string? osv, string? projects, string? sessions) =>
        Assert.Null(Ping.Parse(id, v, os, osv, projects, sessions));
}
