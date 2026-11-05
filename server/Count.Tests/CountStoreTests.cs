using System.Net.Sockets;
using Azure.Data.Tables;

namespace Llamachant.Count.Tests;

/// <summary>
/// The table itself, against Azurite (the storage emulator: <c>npx azurite --silent --location %TEMP%\azurite</c>).
/// Without it running these pass without checking anything, so the rest of the suite runs anywhere.
/// </summary>
public sealed class CountStoreTests : IAsyncLifetime
{
    private static readonly DateTime Hour = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = DateOnly.FromDateTime(Hour);
    private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly TableClient table = new("UseDevelopmentStorage=true", "pingstest" + Guid.NewGuid().ToString("N")[..8]);
    private readonly bool azurite = AzuriteRunning();

    private static Ping P(string id, string version = "1.2.0", int sessions = 2) => new(id, version, "windows", "10.0.26300", 1, sessions);

    private static bool AzuriteRunning()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", 10002).Wait(300) && c.Connected;
        }
        catch (Exception) { return false; }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (azurite) await table.DeleteAsync();
    }

    [Fact]
    public async Task Pinging_twice_in_an_hour_is_one_row_with_the_latest()
    {
        if (!azurite) return;
        var store = new CountStore(table);

        await store.RecordAsync(P(IdA, sessions: 2), Hour);
        await store.RecordAsync(P(IdA, sessions: 5), Hour.AddMinutes(40));
        await store.RecordAsync(P(IdB), Hour);
        await store.RecordAsync(P(IdA), Hour.AddHours(1));

        var rows = await store.ReadAsync(Day, Day);
        Assert.Equal(3, rows.Count);
        Assert.Equal(5, rows.Single(r => r.Id == IdA && r.Hour == "2026-11-02T09").Sessions);
    }

    [Fact]
    public async Task Reading_takes_every_hour_of_the_days_asked_for_and_no_others()
    {
        if (!azurite) return;
        var store = new CountStore(table);
        await store.RecordAsync(P(IdA), new DateTime(2026, 11, 1, 23, 0, 0, DateTimeKind.Utc));
        await store.RecordAsync(P(IdA), new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        await store.RecordAsync(P(IdA), new DateTime(2026, 11, 2, 23, 0, 0, DateTimeKind.Utc));
        await store.RecordAsync(P(IdA), new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc));

        var rows = await store.ReadAsync(Day, Day);
        Assert.Equal(["2026-11-02T00", "2026-11-02T23"], rows.Select(r => r.Hour).Order());
    }

    [Fact]
    public async Task Forgetting_deletes_every_hour_of_the_older_days_only()
    {
        if (!azurite) return;
        var store = new CountStore(table);
        await store.RecordAsync(P(IdA), Hour.AddDays(-1));
        await store.RecordAsync(P(IdB), Hour.AddDays(-1).AddHours(14));
        await store.RecordAsync(P(IdA), new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, await store.ForgetBeforeAsync(Day));
        Assert.Equal(["2026-11-02T00"], (await store.ReadAsync(Day.AddDays(-30), Day)).Select(r => r.Hour));
    }

    [Fact]
    public async Task Nothing_but_the_versions_the_system_and_the_counts_is_stored()
    {
        if (!azurite) return;
        await new CountStore(table).RecordAsync(new Ping(IdA, "1.2.0", "windows", "10.0.26300", 3, 7), Hour);

        var row = (await table.GetEntityAsync<TableEntity>("2026-11-02T09", IdA)).Value;
        Assert.Equal(["Os", "OsVersion", "PartitionKey", "Projects", "RowKey", "Sessions", "Timestamp", "Version", "odata.etag"],
            row.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((3, 7), (row.GetInt32("Projects"), row.GetInt32("Sessions")));
    }
}
