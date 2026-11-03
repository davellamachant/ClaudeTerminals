using System.Net.Sockets;
using Azure.Data.Tables;

namespace Llamachant.Count.Tests;

/// <summary>
/// The table itself, against Azurite (the storage emulator: <c>npx azurite --silent --location %TEMP%\azurite</c>).
/// Without it running these pass without checking anything, so the rest of the suite runs anywhere.
/// </summary>
public sealed class CountStoreTests : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 11, 2);
    private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly TableClient table = new("UseDevelopmentStorage=true", "pingstest" + Guid.NewGuid().ToString("N")[..8]);
    private readonly bool azurite = AzuriteRunning();

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
    public async Task Pinging_twice_in_a_day_is_one_row_with_the_latest_version()
    {
        if (!azurite) return;
        var store = new CountStore(table);

        await store.RecordAsync(new Ping(IdA, "1.1.6", "10.0.26300"), Day);
        await store.RecordAsync(new Ping(IdA, "1.2.0", "10.0.26300"), Day);
        await store.RecordAsync(new Ping(IdB, "1.2.0", "10.0.19045"), Day);

        var rows = await store.ReadAsync(Day, Day);
        Assert.Equal(2, rows.Count);
        Assert.Equal("1.2.0", rows.Single(r => r.Id == IdA).Version);
    }

    [Fact]
    public async Task Reading_takes_only_the_days_asked_for()
    {
        if (!azurite) return;
        var store = new CountStore(table);
        for (var d = 0; d < 5; d++) await store.RecordAsync(new Ping(IdA, "1.2.0", "10.0.26300"), Day.AddDays(d));

        var rows = await store.ReadAsync(Day.AddDays(1), Day.AddDays(3));
        Assert.Equal(["2026-11-03", "2026-11-04", "2026-11-05"], rows.Select(r => r.Day).Order());
    }

    [Fact]
    public async Task Forgetting_deletes_only_the_older_days()
    {
        if (!azurite) return;
        var store = new CountStore(table);
        await store.RecordAsync(new Ping(IdA, "1.2.0", "10.0.26300"), Day.AddDays(-1));
        await store.RecordAsync(new Ping(IdB, "1.2.0", "10.0.26300"), Day.AddDays(-1));
        await store.RecordAsync(new Ping(IdA, "1.2.0", "10.0.26300"), Day);

        Assert.Equal(2, await store.ForgetBeforeAsync(Day));
        Assert.Equal([Tally.Key(Day)], (await store.ReadAsync(Day.AddDays(-30), Day)).Select(r => r.Day));
    }

    [Fact]
    public async Task Nothing_but_the_id_version_and_Windows_version_is_stored()
    {
        if (!azurite) return;
        await new CountStore(table).RecordAsync(new Ping(IdA, "1.2.0", "10.0.26300"), Day);

        var row = await table.GetEntityAsync<TableEntity>(Tally.Key(Day), IdA);
        Assert.Equal(["PartitionKey", "RowKey", "Timestamp", "Version", "Windows", "odata.etag"],
            row.Value.Keys.Order(StringComparer.Ordinal));
    }
}
