using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.RateLimiting.InMemory;
using Mize.Reservations.Storage.InMemory;
using Xunit;

using Mize.Reservations.IntegrationTests;

namespace Mize.Reservations.UnitTests;

/// <summary>
/// The whole API with BOTH adapters swapped for in-process, verification-only ones: in-memory storage and in-memory
/// limiter. No SQLite, no Redis. Proves the host, endpoints and business flow depend only on the ports. Neither adapter is
/// selectable in the product (see the last test); tests opt in by replacing the ports.
/// </summary>
public sealed class FullySwappedApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-swapped-" + Guid.NewGuid().ToString("N"));
    private readonly ApiInstance _api;

    private string DbPath => Path.Combine(_dir, "api.db");

    public FullySwappedApiTests() =>
        _api = new ApiInstance("127.0.0.1:1", DbPath, "unused:", limit: 5, window: 60,
            configureServices: s =>
            {
                s.RemoveAll<IReservationRepository>();
                s.RemoveAll<IStatsRepository>();
                s.RemoveAll<IStorageInitializer>();
                s.AddInMemoryStorage();

                s.RemoveAll<IRateLimiter>();   // the product only offers Redis; tests swap the port itself
                s.AddInMemoryRateLimiting();
            });

    public void Dispose()
    {
        _api.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static object Payload(string supplier, string id, decimal price = 450.00m, string updated = "2026-07-01T10:15:00Z") => new
    {
        supplierId = supplier, reservationId = id, roomId = "room1",
        checkIn = "2026-08-01T00:00:00Z", checkOut = "2026-08-03T00:00:00Z", price, updatedAtUtc = updated
    };

    private record IngestDto(string Status, bool Changed);

    [Fact]
    public async Task Create_duplicate_update_stale_and_stats_work_on_the_swapped_storage()
    {
        var c = _api.CreateClient();

        (await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1"))).StatusCode.Should().Be(HttpStatusCode.Created);

        var dup = await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1"));
        (await dup.Content.ReadFromJsonAsync<IngestDto>()).Should().Be(new IngestDto("Unchanged", false));

        var upd = await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1", price: 400m, updated: "2026-07-02T00:00:00Z"));
        (await upd.Content.ReadFromJsonAsync<IngestDto>()).Should().Be(new IngestDto("Updated", true));

        var stale = await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1", price: 1m, updated: "2026-06-01T00:00:00Z"));
        (await stale.Content.ReadFromJsonAsync<IngestDto>()).Should().Be(new IngestDto("Stale", false));

        (await c.GetFromJsonAsync<StatsDto>("/api/reservations/stats/acme")).Should().Be(new StatsDto("acme", 2, 0, 1, 1));
    }

    [Fact]
    public async Task Throttling_is_counted_through_the_swapped_stats_port_and_suppliers_stay_isolated()
    {
        var c = _api.CreateClient();
        for (var i = 0; i < 5; i++)
            (await c.PostAsJsonAsync("/api/reservations/ingest", Payload("noisy", $"r{i}"))).IsSuccessStatusCode.Should().BeTrue();

        var flood = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(async () => (await c.PostAsJsonAsync("/api/reservations/ingest", Payload("noisy", $"x{i}"))).StatusCode)));
        flood.Should().OnlyContain(x => x == HttpStatusCode.TooManyRequests);

        (await c.PostAsJsonAsync("/api/reservations/ingest", Payload("calm", "r1"))).StatusCode.Should().Be(HttpStatusCode.Created);

        var noisy = await c.GetFromJsonAsync<StatsDto>("/api/reservations/stats/noisy");
        (noisy!.Ingested, noisy.Throttled).Should().Be((5, 40));
    }

    [Fact]
    public async Task Same_reservation_id_under_different_suppliers_is_independent_and_resends_never_recreate()
    {
        var c = _api.CreateClient();
        var codes = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            Task.Run(async () => (await c.PostAsJsonAsync("/api/reservations/ingest", Payload($"s{i}", "same"))).StatusCode)));
        codes.Should().OnlyContain(x => x == HttpStatusCode.Created); // 4 different suppliers: independent

        var dups = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => // s0 total: 1 + 3 = 4, under its limit of 5
            Task.Run(async () => (await c.PostAsJsonAsync("/api/reservations/ingest", Payload("s0", "same"))).StatusCode)));
        dups.Should().OnlyContain(x => x == HttpStatusCode.OK); // Unchanged: never re-created
    }

    [Fact]
    public async Task SQLite_is_never_touched_when_storage_is_swapped()
    {
        var c = _api.CreateClient();
        await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "r1"));
        await c.GetFromJsonAsync<StatsDto>("/api/reservations/stats/acme");

        File.Exists(DbPath).Should().BeFalse("no SQLite connection should ever have been opened"); // proves the swap is real
    }

    [Fact]
    public void The_in_memory_storage_is_not_selectable_in_the_product()
    {
        // Only SQLite (storage) and Redis (rate limiter) are supported by the app; the in-memory storage is a
        // verification adapter reachable only by tests replacing the ports.
        var bad = new ApiInstance("127.0.0.1:1", Path.Combine(_dir, "bad.db"), "x:", storageProvider: "InMemory");
        var act = () => bad.CreateClient();
        act.Should().Throw<Exception>().Which.ToString().Should().Contain("Unknown Storage:Provider 'InMemory'. Available: Sqlite.");
        bad.Dispose();
    }
}
