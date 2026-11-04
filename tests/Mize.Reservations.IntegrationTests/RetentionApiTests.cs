using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mize.Reservations.IntegrationTests;

/// <summary>
/// The resurrection guard over HTTP: with retention enabled, an update for a stay that ended before the cutoff is answered
/// 200 / "Expired" / changed:false (never an error, so suppliers don't retry) and is never stored. Disabled by default.
/// Real Redis limiter and real SQLite.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RetentionApiTests(RedisFixture redis) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-retapi-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefix = $"{Guid.NewGuid():N}:";
    private readonly List<ApiInstance> _instances = new();

    public void Dispose()
    {
        foreach (var i in _instances) i.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private HttpClient Client(string name, IDictionary<string, string?>? retention)
    {
        var api = new ApiInstance(redis.ConnectionString, Path.Combine(_dir, name + ".db"), _prefix, limit: 1000, window: 60,
            extraConfig: retention);
        _instances.Add(api);
        return api.CreateClient();
    }

    private static object Payload(string supplier, string id, string checkIn, string checkOut) => new
    {
        supplierId = supplier, reservationId = id, roomId = "room1", checkIn, checkOut,
        price = 450.00m, updatedAtUtc = "2026-07-01T10:15:00Z"
    };

    private record IngestDto(string Status, bool Changed);

    private static readonly Dictionary<string, string?> Enabled = new() { ["Retention:Enabled"] = "true", ["Retention:RetentionDays"] = "365" };

    [Fact]
    public async Task With_retention_enabled_an_update_for_an_expired_stay_is_200_Expired_and_is_never_stored()
    {
        var c = Client("on", Enabled);

        var resp = await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "old", "2020-01-01T00:00:00Z", "2020-01-03T00:00:00Z"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK); // not an error: a retrying supplier would otherwise loop
        (await resp.Content.ReadFromJsonAsync<IngestDto>()).Should().Be(new IngestDto("Expired", false));
        (await c.GetFromJsonAsync<StatsDto>("/api/reservations/stats/acme")).Should().Be(new StatsDto("acme", 0, 0, 0, 0)); // nothing stored or counted
    }

    [Fact]
    public async Task With_retention_enabled_a_stay_inside_the_window_is_accepted_normally()
    {
        var c = Client("window", Enabled);

        var resp = await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "future", "2099-01-01T00:00:00Z", "2099-01-03T00:00:00Z"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await c.GetFromJsonAsync<StatsDto>("/api/reservations/stats/acme"))!.Ingested.Should().Be(1);
    }

    [Fact]
    public async Task By_default_retention_is_off_and_old_stays_are_accepted_exactly_as_before()
    {
        var c = Client("off", retention: null);

        var resp = await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "old", "2020-01-01T00:00:00Z", "2020-01-03T00:00:00Z"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_shorter_retention_period_expires_more_and_an_invalid_payload_is_still_a_400()
    {
        var c = Client("short", new Dictionary<string, string?> { ["Retention:Enabled"] = "true", ["Retention:RetentionDays"] = "1" });

        var expired = await c.PostAsJsonAsync("/api/reservations/ingest", Payload("acme", "a", "2024-01-01T00:00:00Z", "2024-01-03T00:00:00Z"));
        (await expired.Content.ReadFromJsonAsync<IngestDto>())!.Status.Should().Be("Expired");

        var invalid = await c.PostAsJsonAsync("/api/reservations/ingest", new { supplierId = "acme", reservationId = "b" });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
