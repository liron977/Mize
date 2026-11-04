using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.RateLimiting.InMemory;
using Xunit;

using Mize.Reservations.IntegrationTests;

namespace Mize.Reservations.UnitTests;

/// <summary>
/// Rate limiter swappability and the startup block. Storage stays SQLite here (only the limiter is swapped), so this
/// isolates the limiter port. No Redis anywhere.
/// </summary>
public sealed class RateLimiterSwapApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-limswap-" + Guid.NewGuid().ToString("N"));
    private readonly List<ApiInstance> _instances = new();

    public void Dispose()
    {
        foreach (var i in _instances) i.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private ApiInstance Api(string name, int limit = 5, string? rateLimitProvider = null, bool swapLimiter = false)
    {
        var api = new ApiInstance("127.0.0.1:1", Path.Combine(_dir, name + ".db"), "unused:", limit: limit, window: 60,
            rateLimitProvider: rateLimitProvider,
            configureServices: swapLimiter ? s => { s.RemoveAll<IRateLimiter>(); s.AddInMemoryRateLimiting(); } : null);
        _instances.Add(api);
        return api;
    }

    private static object Payload(string supplier, string id) => new
    {
        supplierId = supplier, reservationId = id, roomId = "room1",
        checkIn = "2026-08-01T00:00:00Z", checkOut = "2026-08-03T00:00:00Z",
        price = 450.00m, updatedAtUtc = "2026-07-01T10:15:00Z"
    };


    [Fact]
    public async Task Ingest_dedup_and_throttle_work_with_the_limiter_swapped_through_the_port_and_no_redis()
    {
        var c = Api("swap", limit: 5, swapLimiter: true).CreateClient();

        (await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1"))).StatusCode.Should().Be(HttpStatusCode.OK);
        for (var i = 2; i <= 4; i++) // requests 3..5 of the 5 allowed
            (await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", $"r{i}"))).IsSuccessStatusCode.Should().BeTrue();

        var throttled = await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r6")); // 6th request, limit 5
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter.Should().NotBeNull();

        // the body: standard ProblemDetails with ONE numeric "status" (429) and the outcome under its own key
        var raw = await throttled.Content.ReadAsStringAsync();
        System.Text.RegularExpressions.Regex.Matches(raw, "\"status\"\\s*:").Should().HaveCount(1, "a duplicate key would let parsers read the wrong value");
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(429);
        doc.RootElement.GetProperty("outcome").GetString().Should().Be("Throttled");
        doc.RootElement.GetProperty("title").GetString().Should().Be("Too many requests.");

        (await c.PostAsJsonAsync("/api/reservations/ingest", Payload("other", "r1"))).StatusCode.Should().Be(HttpStatusCode.Created); // isolated

        (await c.GetFromJsonAsync<StatsDto>("/api/reservations/stats/acme")).Should().Be(new StatsDto("acme", 4, 1, 1, 0));
    }

    [Theory]
    [InlineData("InMemory")]   // the verification-only limiter: blocked at startup
    [InlineData("inmemory")]   // case-insensitive
    [InlineData("Memcached")]  // anything unknown
    public void Only_Redis_can_be_selected_in_the_product_anything_else_fails_fast_at_startup(string provider)
    {
        var bad = Api("bad", rateLimitProvider: provider);

        var act = () => bad.CreateClient();

        act.Should().Throw<Exception>().Which.ToString()
            .Should().Contain($"Unknown RateLimit:Provider '{provider}'. Available: Redis.");
    }
}
