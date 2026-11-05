using Azure;
using Azure.Data.Tables;

namespace Llamachant.Count;

/// <summary>
/// The pings table. A row per copy per UTC hour (PartitionKey the hour, yyyy-MM-ddTHH; RowKey the id), so a copy that
/// pings twice in an hour is still one row, and an hour's users are the rows in its partition.
/// </summary>
internal sealed class CountStore(TableClient table)
{
    public const string TableName = "pings";

    /// <summary>How long a day's rows are kept: a little over a year, enough to compare a month with the same one last year.</summary>
    public const int KeepDays = 400;

    private bool created;

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (created) return;
        await table.CreateIfNotExistsAsync(ct);
        created = true;
    }

    public async Task RecordAsync(Ping ping, DateTime hour, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        var row = new TableEntity(Tally.HourKey(hour), ping.Id)
        {
            ["Version"] = ping.Version,
            ["Os"] = ping.Os,
            ["OsVersion"] = ping.OsVersion,
            ["Projects"] = ping.Projects,
            ["Sessions"] = ping.Sessions,
        };
        await table.UpsertEntityAsync(row, TableUpdateMode.Replace, ct);
    }

    public async Task<List<PingRow>> ReadAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        var filter = TableClient.CreateQueryFilter($"PartitionKey ge {Tally.HourKey(from, 0)} and PartitionKey le {Tally.HourKey(to, 23)}");
        var rows = new List<PingRow>();
        await foreach (var e in table.QueryAsync<TableEntity>(filter,
            select: ["PartitionKey", "RowKey", "Version", "Os", "OsVersion", "Projects", "Sessions"], cancellationToken: ct))
            rows.Add(new PingRow(e.PartitionKey, e.RowKey, e.GetString("Version") ?? "", e.GetString("Os") ?? "", e.GetString("OsVersion") ?? "",
                e.GetInt32("Projects") ?? 0, e.GetInt32("Sessions") ?? 0));
        return rows;
    }

    /// <summary>Deletes the days before <paramref name="before"/>. Returns how many rows went.</summary>
    public async Task<int> ForgetBeforeAsync(DateOnly before, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        // "2026-11-02" sorts before "2026-11-02T00", so this is every hour of the days before.
        var filter = TableClient.CreateQueryFilter($"PartitionKey lt {Tally.DayKey(before)}");
        var gone = 0;
        await foreach (var e in table.QueryAsync<TableEntity>(filter, select: ["PartitionKey", "RowKey"], cancellationToken: ct))
        {
            try
            {
                await table.DeleteEntityAsync(e.PartitionKey, e.RowKey, ETag.All, ct);
                gone++;
            }
            catch (RequestFailedException x) when (x.Status == 404) { }
        }
        return gone;
    }
}
