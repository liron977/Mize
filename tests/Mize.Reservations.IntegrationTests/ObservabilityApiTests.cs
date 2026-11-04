using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Mize.Reservations.IntegrationTests;

/// <summary>
/// /metrics on the REAL composition (real Redis, real SQLite, the real Prometheus exporter). Assertions are lower bounds on
/// purpose: meters are matched by name, so other hosts running in parallel in the same test process can add to the same series.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class MetricsEndpointTests(RedisFixture redis) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-metrics-" + Guid.NewGuid().ToString("N"));
    private readonly List<ApiInstance> _instances = new();

    public void Dispose()
    {
        foreach (var i in _instances) i.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static object Payload(string supplier, string id) => new
    {
        supplierId = supplier, reservationId = id, roomId = "room1",
        checkIn = "2026-08-01T00:00:00Z", checkOut = "2026-08-03T00:00:00Z", price = 450.00m, updatedAtUtc = "2026-07-01T10:15:00Z"
    };

    /// <summary>Value of the first series of <paramref name="metric"/> whose labels contain all the given label="value" fragments.</summary>
    private static double Series(string scrape, string metric, params string[] labels)
    {
        var pattern = new Regex($@"^{Regex.Escape(metric)}\{{(?<l>[^}}]*)\}}\s+(?<v>[0-9.eE+\-]+)", RegexOptions.Multiline);
        return pattern.Matches(scrape)
            .Where(m => labels.All(l => m.Groups["l"].Value.Contains(l)))
            .Select(m => double.Parse(m.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0).Max();
    }

    [Fact]
    public async Task Metrics_are_exposed_in_prometheus_format_and_reflect_real_traffic()
    {
        var api = new ApiInstance(redis.ConnectionString, Path.Combine(_dir, "m.db"), $"m{Guid.NewGuid():N}:", limit: 5, window: 60);
        _instances.Add(api);
        var c = api.CreateClient();
        var supplier = "sup-" + Guid.NewGuid().ToString("N")[..8];

        (await c.PostAsJsonAsync("/api/reservations/ingest", Payload(supplier, "r1"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await c.PostAsJsonAsync("/api/reservations/ingest", Payload(supplier, "r1"))).StatusCode.Should().Be(HttpStatusCode.OK);   // unchanged
        for (var i = 2; i <= 4; i++) await c.PostAsJsonAsync("/api/reservations/ingest", Payload(supplier, $"r{i}"));                // 5 accepted in total
        for (var i = 0; i < 6; i++) (await c.PostAsJsonAsync("/api/reservations/ingest", Payload(supplier, $"x{i}")))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);                                                                  // 1 denied + 5 blocked locally

        var response = await c.GetAsync("/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var scrape = await response.Content.ReadAsStringAsync();

        // the metrics we defined
        scrape.Should().Contain("# TYPE reservations_ingest_requests_total counter");
        Series(scrape, "reservations_ingest_requests_total", "outcome=\"created\"").Should().BeGreaterThanOrEqualTo(4);
        Series(scrape, "reservations_ingest_requests_total", "outcome=\"unchanged\"").Should().BeGreaterThanOrEqualTo(1);
        Series(scrape, "reservations_ingest_requests_total", "outcome=\"throttled\"").Should().BeGreaterThanOrEqualTo(6);

        Series(scrape, "reservations_ratelimit_decisions_total", "result=\"allowed\"").Should().BeGreaterThanOrEqualTo(5);
        Series(scrape, "reservations_ratelimit_decisions_total", "result=\"denied\"").Should().BeGreaterThanOrEqualTo(1);
        Series(scrape, "reservations_ratelimit_decisions_total", "result=\"blocked_locally\"").Should().BeGreaterThanOrEqualTo(5);

        scrape.Should().Contain("# TYPE reservations_ingest_duration_seconds histogram");
        scrape.Should().MatchRegex("reservations_ingest_duration_seconds_bucket\\{[^}]*le=\"0\\.005\"",
            "ingest time is recorded in seconds, so the buckets must be seconds-scale (the default ones assume milliseconds)");
        Series(scrape, "reservations_storage_batch_size_count").Should().BeGreaterThanOrEqualTo(1);
        scrape.Should().Contain("reservations_storage_queue_depth");

        // what the framework and runtime add for free
        scrape.Should().Contain("http_server_request_duration_seconds");
        scrape.Should().MatchRegex("(?m)^process_runtime_dotnet_gc_", "runtime metrics (GC, thread pool, ...)");
        scrape.Should().MatchRegex("(?m)^process_runtime_dotnet_thread_pool_");
    }

    [Fact]
    public async Task Metrics_are_never_labelled_with_a_supplier_id()
    {
        var api = new ApiInstance(redis.ConnectionString, Path.Combine(_dir, "card.db"), $"c{Guid.NewGuid():N}:", limit: 100, window: 60);
        _instances.Add(api);
        var c = api.CreateClient();
        var unique = "very-unique-supplier-" + Guid.NewGuid().ToString("N")[..8];
        await c.PostAsJsonAsync("/api/reservations/ingest", Payload(unique, "r1"));

        var scrape = await (await c.GetAsync("/metrics")).Content.ReadAsStringAsync();

        scrape.Should().NotContain(unique, "supplier ids are unbounded, so labelling metrics with them would grow without limit");
    }
}

/// <summary>What operators see in the logs, on the real stack (real Redis limiter, real SQLite).</summary>
[Collection(RedisCollection.Name)]
public sealed class LoggingApiTests(RedisFixture fixture) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-logs-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefix = $"logs{Guid.NewGuid():N}:";
    private readonly List<ApiInstance> _instances = new();

    public void Dispose()
    {
        foreach (var i in _instances) i.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private (HttpClient client, CapturingLoggerProvider logs) Start(string name, int limit = 100,
        IDictionary<string, string?>? config = null, string? redis = null)
    {
        var logs = new CapturingLoggerProvider();
        var api = new ApiInstance(redis ?? fixture.ConnectionString, Path.Combine(_dir, name + ".db"), _prefix, limit: limit, window: 60,
            configureServices: s => s.AddSingleton<ILoggerProvider>(logs),
            extraConfig: config);
        _instances.Add(api);
        return (api.CreateClient(), logs);
    }

    private static object Payload(string supplier, string id) => new
    {
        supplierId = supplier, reservationId = id, roomId = "room1",
        checkIn = "2026-08-01T00:00:00Z", checkOut = "2026-08-03T00:00:00Z", price = 450.00m, updatedAtUtc = "2026-07-01T10:15:00Z"
    };

    private const string DebugKey = "Logging:LogLevel:Mize.Reservations.Api.Observability.IngestLogger";

    [Fact]
    public void Startup_logs_one_summary_line_saying_what_this_instance_runs_with()
    {
        var (_, logs) = Start("startup", config: new Dictionary<string, string?> { ["Retention:Enabled"] = "true", ["Retention:RetentionDays"] = "400" });

        var summary = logs.Lines.Where(l => l.Message.StartsWith("Reservation ingest API started")).ToList();

        summary.Should().ContainSingle();
        summary[0].Level.Should().Be(LogLevel.Information);
        summary[0].Message.Should().Contain("storage=Sqlite").And.Contain("retention=True (400 days)").And.Contain("writeBatch=256");
    }

    [Fact]
    public async Task A_throttled_supplier_is_named_in_one_warning_not_one_line_per_request()
    {
        var (c, logs) = Start("throttle", limit: 3);
        var supplier = "noisy-" + Guid.NewGuid().ToString("N")[..6];

        for (var i = 0; i < 40; i++) await c.PostAsJsonAsync("/api/reservations/ingest", Payload(supplier, $"r{i}")); // 3 accepted, 37 throttled

        var warnings = logs.Lines.Where(l => l.Level == LogLevel.Warning && l.Message.Contains("is being throttled")).ToList();
        warnings.Should().ContainSingle("37 throttled requests must produce one line, not 37");
        warnings[0].Message.Should().Contain(supplier);
    }

    [Fact]
    public async Task Per_request_lines_are_off_by_default()
    {
        var (c, logs) = Start("default-level");

        await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1"));
        await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1"));

        logs.Lines.Where(l => l.Message.StartsWith("Ingest acme/")).Should().BeEmpty();
    }

    [Fact]
    public async Task Per_request_lines_appear_with_supplier_reservation_outcome_and_time_when_debug_is_enabled()
    {
        var (c, logs) = Start("debug", config: new Dictionary<string, string?> { [DebugKey] = "Debug" });

        await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1"));
        await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1"));

        var lines = logs.Lines.Where(l => l.Message.StartsWith("Ingest acme/")).Select(l => l.Message).ToList();
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("acme/r1 -> Created in ").And.EndWith(" ms");
        lines[1].Should().Contain("acme/r1 -> Unchanged in ");
    }

    [Fact]
    public async Task A_rate_limiter_outage_is_logged_once_with_the_error_but_every_request_is_still_rejected_and_counted()
    {
        // the real Redis limiter pointed at a port nothing listens on
        var (c, logs) = Start("outage", redis: "127.0.0.1:1");

        var codes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Task.Run(async () => (await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", $"r{i}"))).StatusCode)));

        codes.Should().OnlyContain(x => x == HttpStatusCode.ServiceUnavailable);
        var errors = logs.Lines.Where(l => l.Level == LogLevel.Error && l.Message.Contains("Rate limiter unavailable")).ToList();
        errors.Should().ContainSingle("12 identical failures must not produce 12 stack traces");
        errors[0].Exception.Should().NotBeNull("the one line that is logged keeps the exception");

        var scrape = await (await c.GetAsync("/metrics")).Content.ReadAsStringAsync();
        scrape.Should().MatchRegex("reservations_ingest_requests_total\\{[^}]*outcome=\"error\"");
        scrape.Should().MatchRegex("reservations_ratelimit_decisions_total\\{[^}]*result=\"unavailable\"");
    }
}
