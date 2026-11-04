using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Mize.Reservations.IntegrationTests;

/// <summary>
/// The main flow end to end on the REAL composition (real Redis, real SQLite, two instances where it matters), asserting not only
/// what the API answers but what is actually STORED: every column of every row, the counters row, and what is absent.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class MainFlowDatabaseTests(RedisFixture redis) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-mainflow-" + Guid.NewGuid().ToString("N"));
    private readonly List<ApiInstance> _instances = new();

    public void Dispose()
    {
        foreach (var i in _instances) i.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string DbPath(string name) => Path.Combine(_dir, name + ".db");

    private HttpClient NewClient(string db, string prefix, int limit = 100, string? redisCs = null)
    {
        var api = new ApiInstance(redisCs ?? redis.ConnectionString, db, prefix, limit, window: 60);
        _instances.Add(api);
        return api.CreateClient();
    }

    // ---- reading the database directly (a separate read connection, as an operator would)

    private sealed class StoredRow
    {
        public string SupplierId { get; set; } = "";
        public string ReservationId { get; set; } = "";
        public string RoomId { get; set; } = "";
        public string CheckIn { get; set; } = "";
        public string CheckOut { get; set; } = "";
        public long PriceCents { get; set; }
        public string UpdatedAtUtc { get; set; } = "";
        public string CreatedAtUtc { get; set; } = "";
    }

    private sealed class StoredStats
    {
        public string SupplierId { get; set; } = "";
        public long Ingested { get; set; }
        public long Unchanged { get; set; }
        public long Stale { get; set; }
        public long Throttled { get; set; }
    }

    private static async Task<SqliteConnection> Open(string db)
    {
        var c = new SqliteConnection($"Data Source={db};Pooling=False");
        await c.OpenAsync();
        return c;
    }

    private static async Task<List<StoredRow>> Rows(string db, string supplier)
    {
        await using var c = await Open(db);
        return (await c.QueryAsync<StoredRow>(
            "SELECT SupplierId, ReservationId, RoomId, CheckIn, CheckOut, PriceCents, UpdatedAtUtc, CreatedAtUtc FROM Reservations WHERE SupplierId=@supplier ORDER BY ReservationId",
            new { supplier })).ToList();
    }

    private static async Task<StoredRow> Row(string db, string supplier, string reservation) =>
        (await Rows(db, supplier)).Single(r => r.ReservationId == reservation);

    private static async Task<long> TotalRows(string db)
    {
        await using var c = await Open(db);
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Reservations");
    }

    private static async Task<StoredStats?> StatsRow(string db, string supplier)
    {
        await using var c = await Open(db);
        return await c.QuerySingleOrDefaultAsync<StoredStats>(
            "SELECT SupplierId, Ingested, Unchanged, Stale, Throttled FROM SupplierStats WHERE SupplierId=@supplier", new { supplier });
    }

    // ---- requests

    private static string Json(string supplier, string reservation, string room = "room1", string checkIn = "2026-08-01T00:00:00Z",
        string checkOut = "2026-08-03T00:00:00Z", string price = "450.00", string updated = "2026-07-01T10:15:00Z") =>
        $"{{\"supplierId\":\"{supplier}\",\"reservationId\":\"{reservation}\",\"roomId\":\"{room}\",\"checkIn\":\"{checkIn}\"," +
        $"\"checkOut\":\"{checkOut}\",\"price\":{price},\"updatedAtUtc\":\"{updated}\"}}";

    private static async Task<HttpResponseMessage> Post(HttpClient c, string json)
    {
        var content = new StringContent(json, new UTF8Encoding(false));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        return await c.PostAsync("/api/reservations/ingest", content);
    }


    // ======================================================================================================================

    [Fact]
    public async Task The_spec_scenario_leaves_exactly_the_right_rows_and_counters_in_the_database_at_every_step()
    {
        var db = DbPath("scenario");
        var c = NewClient(db, $"s{Guid.NewGuid():N}:");
        var id = Guid.NewGuid().ToString("N")[..8];
        var rawSupplier = $"  AcMe-{id}  ";                 // padded and mixed-case on the wire ...
        var supplier = $"acme-{id}";                         // ... trimmed and lower-cased in storage

        // 1) create. checkIn is written with an offset, so storage must hold the UTC instant.
        var created = await Post(c, Json(rawSupplier, "R-1", room: "Room 7", checkIn: "2026-08-01T02:00:00+02:00", checkOut: "2026-08-03T00:00:00Z"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var r1 = await Row(db, supplier, "R-1");
        r1.SupplierId.Should().Be(supplier);
        r1.ReservationId.Should().Be("R-1");                 // reservation ids keep their case
        r1.RoomId.Should().Be("Room 7");
        r1.CheckIn.Should().Be("2026-08-01T00:00:00.0000000Z");
        r1.CheckOut.Should().Be("2026-08-03T00:00:00.0000000Z");
        r1.PriceCents.Should().Be(45000);                    // integer cents, exact
        r1.UpdatedAtUtc.Should().Be("2026-07-01T10:15:00.0000000Z");
        DateTime.Parse(r1.CreatedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
        (await Rows(db, supplier)).Should().HaveCount(1);

        // 2) exact duplicate: nothing changes in the row, only the counter moves
        var dup = await Post(c, Json(rawSupplier, "R-1", room: "Room 7", checkIn: "2026-08-01T02:00:00+02:00"));
        (await dup.Content.ReadAsStringAsync()).Should().Contain("Unchanged");
        (await Row(db, supplier, "R-1")).Should().BeEquivalentTo(r1);
        (await Rows(db, supplier)).Should().HaveCount(1);

        // 3) same details, only a newer timestamp: the freshness marker advances, nothing else (CreatedAt included)
        var touched = await Post(c, Json(rawSupplier, "R-1", room: "Room 7", checkIn: "2026-08-01T02:00:00+02:00", updated: "2026-07-05T09:00:00Z"));
        (await touched.Content.ReadAsStringAsync()).Should().Contain("Unchanged");
        var r1Touched = await Row(db, supplier, "R-1");
        r1Touched.UpdatedAtUtc.Should().Be("2026-07-05T09:00:00.0000000Z");
        r1Touched.Should().BeEquivalentTo(r1, o => o.Excluding(r => r.UpdatedAtUtc));

        // 4) a real update: the details change in place, CreatedAt does not
        var updated = await Post(c, Json(rawSupplier, "R-1", room: "Room 9", checkIn: "2026-08-01T02:00:00+02:00", price: "400.00", updated: "2026-07-10T09:00:00Z"));
        (await updated.Content.ReadAsStringAsync()).Should().Contain("Updated");
        var r1Updated = await Row(db, supplier, "R-1");
        (r1Updated.RoomId, r1Updated.PriceCents, r1Updated.UpdatedAtUtc).Should().Be(("Room 9", 40000, "2026-07-10T09:00:00.0000000Z"));
        r1Updated.CreatedAtUtc.Should().Be(r1.CreatedAtUtc);
        (await Rows(db, supplier)).Should().HaveCount(1);    // updated in place, not a second row

        // 5) stale: older timestamp with different details is ignored; the row is exactly as it was
        var stale = await Post(c, Json(rawSupplier, "R-1", room: "Old room", checkIn: "2026-08-01T02:00:00+02:00", price: "999.00", updated: "2026-06-01T09:00:00Z"));
        (await stale.Content.ReadAsStringAsync()).Should().Contain("Stale");
        (await Row(db, supplier, "R-1")).Should().BeEquivalentTo(r1Updated);

        // 6) a second reservation of the same supplier, and the SAME reservation id under another supplier (composite key)
        (await Post(c, Json(rawSupplier, "R-2", price: "300.00"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var other = $"other-{id}";
        (await Post(c, Json(other, "R-1", price: "1.00"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Rows(db, supplier)).Select(r => r.ReservationId).Should().Equal("R-1", "R-2");
        (await Row(db, other, "R-1")).PriceCents.Should().Be(100);                 // independent of acme's R-1
        (await Row(db, supplier, "R-1")).PriceCents.Should().Be(40000);            // and acme's R-1 was not touched by it

        // 7) invalid requests store nothing and count nothing
        var rowsBefore = await TotalRows(db);
        var statsBefore = await StatsRow(db, supplier);
        (await Post(c, Json(rawSupplier, "R-bad", checkIn: "2026-08-03T00:00:00Z", checkOut: "2026-08-01T00:00:00Z"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Post(c, Json(rawSupplier, "R-bad2", checkIn: "\\\"2026-08-01T00:00:00\\\""))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TotalRows(db)).Should().Be(rowsBefore);
        (await StatsRow(db, supplier)).Should().BeEquivalentTo(statsBefore);

        // 8) the counters row in the database is exactly what the API reports
        var api = await c.GetFromJsonAsync<StatsDto>($"/api/reservations/stats/{rawSupplier.Trim()}");
        var stored = (await StatsRow(db, supplier))!;
        (stored.Ingested, stored.Unchanged, stored.Stale, stored.Throttled).Should().Be((3, 2, 1, 0)); // R-1 created+updated, R-2 created; 2 unchanged; 1 stale
        (api!.Ingested, api.Unchanged, api.Stale, api.Throttled).Should().Be((stored.Ingested, stored.Unchanged, stored.Stale, stored.Throttled));
    }

    [Fact]
    public async Task The_101st_request_is_rejected_and_the_database_holds_exactly_the_100_that_were_accepted()
    {
        var db = DbPath("throttle");
        var c = NewClient(db, $"t{Guid.NewGuid():N}:");
        var noisy = "noisy-" + Guid.NewGuid().ToString("N")[..6];
        var calm = "calm-" + Guid.NewGuid().ToString("N")[..6];

        var codes = new List<HttpStatusCode>();
        for (var i = 1; i <= 101; i++) codes.Add((await Post(c, Json(noisy, $"R{i:D3}"))).StatusCode);
        var calmResponse = await Post(c, Json(calm, "R1"));

        codes.Take(100).Should().OnlyContain(x => x == HttpStatusCode.Created);
        codes[100].Should().Be(HttpStatusCode.TooManyRequests);
        calmResponse.StatusCode.Should().Be(HttpStatusCode.Created);               // another supplier is not affected

        var rows = await Rows(db, noisy);
        rows.Should().HaveCount(100);
        rows.Select(r => r.ReservationId).Should().NotContain("R101");              // the rejected request stored nothing
        var stats = (await StatsRow(db, noisy))!;
        (stats.Ingested, stats.Throttled).Should().Be((100, 1));
        (await Rows(db, calm)).Should().HaveCount(1);
        (await StatsRow(db, calm))!.Throttled.Should().Be(0);
    }

    [Fact]
    public async Task Two_instances_sharing_redis_and_the_database_accept_exactly_100_and_store_exactly_those()
    {
        var db = DbPath("twoinst");
        var prefix = $"x{Guid.NewGuid():N}:";
        var c1 = NewClient(db, prefix);
        var c2 = NewClient(db, prefix);
        var supplier = "shared-" + Guid.NewGuid().ToString("N")[..6];

        var results = await Task.WhenAll(Enumerable.Range(0, 150).Select(i => Task.Run(async () =>
        {
            var id = $"R{i:D3}";
            var response = await Post(i % 2 == 0 ? c1 : c2, Json(supplier, id));
            return (id, response.StatusCode);
        })));

        var accepted = results.Where(r => r.StatusCode == HttpStatusCode.Created).Select(r => r.id).ToList();
        var rejected = results.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).Select(r => r.id).ToList();
        accepted.Should().HaveCount(100);
        rejected.Should().HaveCount(50);

        var stored = (await Rows(db, supplier)).Select(r => r.ReservationId).ToList();
        stored.Should().BeEquivalentTo(accepted);                                   // the stored set IS the accepted set: none lost, none extra
        stored.Intersect(rejected).Should().BeEmpty();
        var stats = (await StatsRow(db, supplier))!;
        (stats.Ingested, stats.Throttled).Should().Be((100, 50));
    }

    [Fact]
    public async Task Forty_parallel_identical_requests_across_two_instances_store_exactly_one_row()
    {
        var db = DbPath("dups");
        var prefix = $"d{Guid.NewGuid():N}:";
        var c1 = NewClient(db, prefix);
        var c2 = NewClient(db, prefix);
        var supplier = "dup-" + Guid.NewGuid().ToString("N")[..6];

        var codes = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(async () => (await Post(i % 2 == 0 ? c1 : c2, Json(supplier, "R1"))).StatusCode)));

        codes.Count(x => x == HttpStatusCode.Created).Should().Be(1);
        codes.Count(x => x == HttpStatusCode.OK).Should().Be(39);
        (await Rows(db, supplier)).Should().HaveCount(1);
        var stats = (await StatsRow(db, supplier))!;
        (stats.Ingested, stats.Unchanged).Should().Be((1, 39));                     // no counter update lost under contention
    }

    [Fact]
    public async Task When_redis_is_down_the_service_fails_closed_and_writes_nothing_at_all()
    {
        var db = DbPath("redisdown");
        var c = NewClient(db, "unused:", redisCs: "127.0.0.1:1");                   // nothing listens on port 1
        var supplier = "down-" + Guid.NewGuid().ToString("N")[..6];

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Task.Run(() => Post(c, Json(supplier, $"R{i}")))));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.ServiceUnavailable);
        responses.Should().OnlyContain(r => r.Headers.RetryAfter != null);
        (await TotalRows(db)).Should().Be(0);
        (await StatsRow(db, supplier)).Should().BeNull();                           // not even a counter was written
    }

    [Fact]
    public async Task Unknown_suppliers_have_zero_stats_and_no_database_row_is_created_by_asking()
    {
        var db = DbPath("stats");
        var c = NewClient(db, $"u{Guid.NewGuid():N}:");

        var stats = await c.GetFromJsonAsync<StatsDto>("/api/reservations/stats/never-seen");

        stats.Should().Be(new StatsDto("never-seen", 0, 0, 0, 0));
        (await StatsRow(db, "never-seen")).Should().BeNull();                       // reading must not write
    }
}
