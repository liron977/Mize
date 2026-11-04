using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mize.Reservations.Core.Models;
using Xunit;

namespace Mize.Reservations.IntegrationTests;

/// <summary>
/// What the service does when something underneath it breaks, over real HTTP with real Redis and real SQLite: a full write queue,
/// a database locked by someone else. In every case the caller gets a
/// clear problem+json answer, nothing internal leaks, nothing half-written is stored, and the service recovers on its own.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class FailureModesApiTests(RedisFixture redis) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-failure-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefix = $"{Guid.NewGuid():N}:";
    private readonly List<ApiInstance> _instances = new();

    public void Dispose()
    {
        foreach (var i in _instances) i.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string DbPath(string name) => Path.Combine(_dir, name + ".db");

    private (HttpClient client, CapturingLoggerProvider logs) Start(string db, IDictionary<string, string?>? config = null)
    {
        var logs = new CapturingLoggerProvider();
        var api = new ApiInstance(redis.ConnectionString, db, _prefix, limit: 100_000, window: 60,
            configureServices: s => s.AddSingleton<ILoggerProvider>(logs),
            extraConfig: config);
        _instances.Add(api);
        return (api.CreateClient(), logs);
    }

    private static string Json(string supplier, string reservation) =>
        $"{{\"supplierId\":\"{supplier}\",\"reservationId\":\"{reservation}\",\"roomId\":\"room1\",\"checkIn\":\"2026-08-01T00:00:00Z\"," +
        "\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":450.00,\"updatedAtUtc\":\"2026-07-01T10:15:00Z\"}";

    private static async Task<HttpResponseMessage> Post(HttpClient c, string json)
    {
        var content = new StringContent(json, new UTF8Encoding(false));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        return await c.PostAsync("/api/reservations/ingest", content);
    }

    private static async Task<List<string>> StoredIds(string db)
    {
        await using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        await c.OpenAsync();
        return (await c.QueryAsync<string>("SELECT ReservationId FROM Reservations")).ToList();
    }

    private static async Task<string> Scrape(HttpClient c) => await (await c.GetAsync("/metrics")).Content.ReadAsStringAsync();

    // ---- the write queue is full -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_full_write_queue_answers_503_with_retry_after_and_what_was_accepted_is_exactly_what_is_stored()
    {
        var db = DbPath("queuefull");
        var (c, logs) = Start(db, new Dictionary<string, string?> { ["Sqlite:QueueCapacity"] = "2", ["Sqlite:MaxBatchSize"] = "1" });
        var supplier = "flood-" + Guid.NewGuid().ToString("N")[..6];

        var results = await Task.WhenAll(Enumerable.Range(0, 400).Select(i => Task.Run(async () =>
        {
            var id = $"R{i:D3}";
            var r = await Post(c, Json(supplier, id));
            return (id, r.StatusCode, retryAfter: r.Headers.RetryAfter?.Delta?.TotalSeconds);
        })));

        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.ServiceUnavailable);
        var accepted = results.Where(r => r.StatusCode == HttpStatusCode.Created).Select(r => r.id).ToList();
        var refused = results.Where(r => r.StatusCode == HttpStatusCode.ServiceUnavailable).ToList();
        accepted.Should().NotBeEmpty("a bounded queue still serves what fits");
        refused.Should().NotBeEmpty("400 simultaneous writers cannot fit in a queue of 2");
        refused.Should().OnlyContain(r => r.retryAfter == 2, "the caller is told when to retry");

        (await StoredIds(db)).Should().BeEquivalentTo(accepted);                    // nothing lost, nothing stored that was refused

        var warnings = logs.Lines.Where(l => l.Level == LogLevel.Warning && l.Message.Contains("Storage busy or write queue full")).ToList();
        warnings.Should().HaveCountGreaterThan(0).And.HaveCountLessThanOrEqualTo(2, "a flood of refusals is logged once per interval, not per request");
        (await Scrape(c)).Should().MatchRegex("reservations_storage_rejections_total\\{[^}]*reason=\"queue_full\"[^}]*\\}\\s+\\d+");

        // and it recovers by itself once the pressure is gone
        (await Post(c, Json(supplier, "after-the-flood"))).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---- the database is locked by someone else ----------------------------------------------------------------------------

    [Fact]
    public async Task A_database_locked_by_another_process_is_a_503_after_the_busy_timeout_reads_keep_working_and_it_recovers_when_released()
    {
        var db = DbPath("locked");
        var (c, logs) = Start(db, new Dictionary<string, string?> { ["Sqlite:BusyTimeoutSeconds"] = "1" });
        var supplier = "locked-" + Guid.NewGuid().ToString("N")[..6];

        (await Post(c, Json(supplier, "before"))).StatusCode.Should().Be(HttpStatusCode.Created);

        await using (var locker = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            await locker.OpenAsync();
            await locker.ExecuteAsync("BEGIN IMMEDIATE");                            // another writer (an admin tool, a worker) holds the lock

            var clock = Stopwatch.StartNew();
            var blocked = await Post(c, Json(supplier, "during"));
            clock.Stop();

            blocked.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            blocked.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().Be(2);
            blocked.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            (await blocked.Content.ReadAsStringAsync()).Should().NotContain("SQLite").And.NotContain("database is locked");
            clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(800)).And.BeLessThan(TimeSpan.FromSeconds(10)); // it waited for the busy timeout, not forever

            // reads are not blocked by a writer in WAL mode
            (await c.GetAsync($"/api/reservations/stats/{supplier}")).StatusCode.Should().Be(HttpStatusCode.OK);

            await locker.ExecuteAsync("ROLLBACK");
        }

        (await Post(c, Json(supplier, "after"))).StatusCode.Should().Be(HttpStatusCode.Created);   // recovered with no restart

        (await StoredIds(db)).Should().BeEquivalentTo("before", "after");            // the refused write left nothing behind
        logs.Lines.Count(l => l.Level == LogLevel.Warning && l.Message.Contains("Storage busy or write queue full")).Should().Be(1);
        (await Scrape(c)).Should().MatchRegex("reservations_storage_rejections_total\\{[^}]*reason=\"busy\"[^}]*\\}\\s+\\d+");
    }
}
