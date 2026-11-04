using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Mize.Reservations.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class IngestApiTests(RedisFixture redis) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-api-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefix = $"api{Guid.NewGuid():N}:";
    private readonly List<ApiInstance> _instances = new();

    private string Db => Path.Combine(_dir, "api.db");

    private HttpClient NewClient(int limit = 100, int window = 60)
    {
        var inst = new ApiInstance(redis.ConnectionString, Db, _prefix, limit, window);
        _instances.Add(inst);
        return inst.CreateClient();
    }

    public void Dispose()
    {
        foreach (var i in _instances) i.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static object Payload(string supplier, string id = "r1", decimal price = 450.00m,
        string updated = "2026-07-01T10:15:00Z", string room = "room1") => new
    {
        supplierId = supplier, reservationId = id, roomId = room,
        checkIn = "2026-08-01T00:00:00Z", checkOut = "2026-08-03T00:00:00Z",
        price, updatedAtUtc = updated
    };

    private static string S() => "sup-" + Guid.NewGuid().ToString("N")[..8];


    [Fact]
    public async Task The_101st_request_in_a_minute_is_429_with_retry_after_and_counted()
    {
        var c = NewClient(); var s = S();
        for (var i = 0; i < 100; i++)
            (await c.PostAsJsonAsync("/api/reservations/ingest", Payload(s, id: $"r{i}"))).IsSuccessStatusCode.Should().BeTrue();

        var resp = await c.PostAsJsonAsync("/api/reservations/ingest", Payload(s, id: "r101"));

        resp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        resp.Headers.RetryAfter.Should().NotBeNull();
        resp.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);

        var stats = await c.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{s}");
        (stats!.Ingested, stats.Throttled).Should().Be((100, 1));
    }

    [Fact]
    public async Task A_throttle_flood_is_fully_counted_and_does_not_disturb_another_supplier()
    {
        var c = NewClient(); string noisy = S(), calm = S();
        for (var i = 0; i < 100; i++)
            (await c.PostAsJsonAsync("/api/reservations/ingest", Payload(noisy, id: $"r{i}"))).IsSuccessStatusCode.Should().BeTrue();

        // 300 rejected requests (served by the local block cache after the first denial) racing with a calm supplier
        var flood = Enumerable.Range(0, 300).Select(i => Task.Run(async () =>
            (await c.PostAsJsonAsync("/api/reservations/ingest", Payload(noisy, id: $"x{i}"))).StatusCode)).ToList();
        var calmSends = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            (await c.PostAsJsonAsync("/api/reservations/ingest", Payload(calm, id: $"c{i}"))).StatusCode)).ToList();

        (await Task.WhenAll(flood)).Should().OnlyContain(x => x == HttpStatusCode.TooManyRequests);
        (await Task.WhenAll(calmSends)).Should().OnlyContain(x => x == HttpStatusCode.Created); // no 429/503 for the calm one

        var sn = await c.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{noisy}");
        var sc = await c.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{calm}");
        (sn!.Ingested, sn.Throttled).Should().Be((100, 300)); // every rejection counted exactly once
        (sc!.Ingested, sc.Throttled).Should().Be((20, 0));
    }

    [Fact]
    public async Task Limit_is_shared_across_two_instances_and_isolated_per_supplier()
    {
        var c1 = NewClient(); var c2 = NewClient();
        string a = S(), b = S();

        var sends = Enumerable.Range(0, 150).Select(i => Task.Run(async () =>
            (await (i % 2 == 0 ? c1 : c2).PostAsJsonAsync("/api/reservations/ingest", Payload(a, id: $"a{i}"))).StatusCode));
        var codes = await Task.WhenAll(sends);

        codes.Count(x => x == HttpStatusCode.Created).Should().Be(100);
        codes.Count(x => x == HttpStatusCode.TooManyRequests).Should().Be(50);

        // supplier B is unaffected by A being throttled
        (await c1.PostAsJsonAsync("/api/reservations/ingest", Payload(b))).StatusCode.Should().Be(HttpStatusCode.Created);

        var sa = await c2.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{a}");
        var sb = await c2.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{b}");
        (sa!.Ingested, sa.Throttled).Should().Be((100, 50));
        (sb!.Ingested, sb.Throttled).Should().Be((1, 0));
    }

    [Fact]
    public async Task Concurrent_identical_payloads_across_instances_create_once()
    {
        var c1 = NewClient(); var c2 = NewClient(); var s = S();

        var codes = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
            (await (i % 2 == 0 ? c1 : c2).PostAsJsonAsync("/api/reservations/ingest", Payload(s))).StatusCode)));

        codes.Count(x => x == HttpStatusCode.Created).Should().Be(1);
        codes.Count(x => x == HttpStatusCode.OK).Should().Be(39);
    }

    [Fact]
    public async Task Supplier_id_case_and_whitespace_share_one_quota()
    {
        var c = NewClient(limit: 3, window: 60); var s = S();
        foreach (var variant in new[] { s.ToUpperInvariant(), $"  {s}  ", s })
            (await c.PostAsJsonAsync("/api/reservations/ingest", Payload(variant, id: variant.Trim()))).IsSuccessStatusCode.Should().BeTrue();

        (await c.PostAsJsonAsync("/api/reservations/ingest", Payload(s.ToUpperInvariant(), id: "x")))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Stats_for_unknown_supplier_are_zero()
    {
        var c = NewClient();
        var stats = await c.GetFromJsonAsync<StatsDto>("/api/reservations/stats/never-seen");
        stats.Should().Be(new StatsDto("never-seen", 0, 0, 0, 0));
    }

    [Fact]
    public async Task Invalid_payload_returns_400_problem_details()
    {
        var c = NewClient(); var s = S();
        var resp = await c.PostAsJsonAsync("/api/reservations/ingest", new { supplierId = s, reservationId = "r1" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("roomId").And.Contain("price");
    }

    [Fact]
    public async Task Redis_outage_fails_closed_with_503_and_nothing_is_written()
    {
        var inst = new ApiInstance("127.0.0.1:1", Db, _prefix); // nothing listens on port 1
        _instances.Add(inst);
        var c = inst.CreateClient(); var s = S();

        var resp = await c.PostAsJsonAsync("/api/reservations/ingest", Payload(s));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        resp.Headers.RetryAfter.Should().NotBeNull();
        (await c.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{s}"))!.Ingested.Should().Be(0);
    }
}
