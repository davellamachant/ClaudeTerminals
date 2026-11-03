namespace Llamachant.Count.Tests;

/// <summary>Only what the app sends is taken: a 32 hex id and two version numbers.</summary>
public sealed class PingTests
{
    private const string Id = "1868c4d72ca345de80adb48806e14052";

    [Fact]
    public void What_the_app_sends_is_a_ping()
    {
        var p = Ping.Parse(Id, "1.2.0", "10.0.26300");
        Assert.Equal(new Ping(Id, "1.2.0", "10.0.26300"), p);
    }

    [Fact]
    public void The_id_is_kept_in_lower_case_so_one_copy_is_one_row() =>
        Assert.Equal(Id, Ping.Parse(Id.ToUpperInvariant(), "1.2.0", "10.0.26300")?.Id);

    [Theory]
    [InlineData(null, "1.2.0", "10.0.26300")]
    [InlineData(Id, null, "10.0.26300")]
    [InlineData(Id, "1.2.0", null)]
    [InlineData("1868c4d72ca345de80adb48806e1405", "1.2.0", "10.0.26300")]      // 31 characters
    [InlineData("1868c4d72ca345de80adb48806e14052a", "1.2.0", "10.0.26300")]    // 33
    [InlineData("1868c4d72ca345de80adb48806e1405z", "1.2.0", "10.0.26300")]     // not hex
    [InlineData(Id, "1.2.0-beta", "10.0.26300")]
    [InlineData(Id, "1.2.0.0.0", "10.0.26300")]                                 // five numbers
    [InlineData(Id, "1.2.0", "Windows 11")]
    [InlineData(Id, "1.2.0", "dave@example.com")]
    [InlineData(Id, "", "10.0.26300")]
    public void Anything_else_is_refused(string? id, string? version, string? windows) =>
        Assert.Null(Ping.Parse(id, version, windows));
}
