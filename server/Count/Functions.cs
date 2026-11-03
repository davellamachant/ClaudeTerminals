using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Llamachant.Count;

internal sealed class Functions(CountStore store, TimeProvider clock, ILogger<Functions> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(Now);

    /// <summary>
    /// GET /ping?id=…&amp;v=…&amp;os=…&amp;osv=…&amp;projects=…&amp;sessions=…: a copy of the app that has been allowed to
    /// count itself, once an hour. Records them for this hour and answers 204; anything else is a 400 and records
    /// nothing. The caller's address and headers aren't read, logged or kept.
    /// </summary>
    [Function("ping")]
    public async Task<HttpResponseData> PingAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")] HttpRequestData request, CancellationToken ct)
    {
        var q = System.Web.HttpUtility.ParseQueryString(request.Url.Query);
        if (Ping.Parse(q["id"], q["v"], q["os"], q["osv"], q["projects"], q["sessions"]) is not { } ping)
            return request.CreateResponse(HttpStatusCode.BadRequest);
        await store.RecordAsync(ping, Now, ct);
        return request.CreateResponse(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// GET /stats?days=30 (with the function key), as JSON: users in the whole period and per day (and per hour for a
    /// week or less), the projects and sessions they had open, and their versions and systems. Up to
    /// <see cref="CountStore.KeepDays"/> days, ending today.
    /// </summary>
    [Function("stats")]
    public async Task<HttpResponseData> StatsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "stats")] HttpRequestData request, CancellationToken ct)
    {
        var q = System.Web.HttpUtility.ParseQueryString(request.Url.Query);
        var days = int.TryParse(q["days"], out var n) ? Math.Clamp(n, 1, CountStore.KeepDays) : 30;
        var to = Today;
        var from = to.AddDays(1 - days);
        var tally = Tally.Of(await store.ReadAsync(from, to, ct), from, to);

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(tally, Json), ct);
        return response;
    }

    /// <summary>Every day at 03:30 UTC, deletes the days older than <see cref="CountStore.KeepDays"/>.</summary>
    [Function("forget")]
    public async Task ForgetAsync([TimerTrigger("0 30 3 * * *")] TimerInfo timer, CancellationToken ct)
    {
        var gone = await store.ForgetBeforeAsync(Today.AddDays(-CountStore.KeepDays), ct);
        if (gone > 0) log.LogInformation("Forgot {Rows} rows older than {Days} days", gone, CountStore.KeepDays);
    }
}
