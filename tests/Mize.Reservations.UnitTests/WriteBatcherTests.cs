using FluentAssertions;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Storage.Sqlite;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>Group-commit behaviour against real SQLite (no Redis/Docker needed).</summary>
public sealed class WriteBatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-wb-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly List<WriteBatcher> _batchers = new();

    public WriteBatcherTests()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_dir, "t.db"));
        DbInitializer.InitializeAsync(_factory).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var b in _batchers) b.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private WriteBatcher NewBatcher(int maxBatch = 256, int capacity = 2000, bool start = true)
    {
        var b = new WriteBatcher(_factory, new WriteBatcherOptions { MaxBatchSize = maxBatch, QueueCapacity = capacity });
        if (start) b.Start();
        _batchers.Add(b);
        return b;
    }

    private static Reservation R(string supplier, string id, long price = 45000, string room = "room1") =>
        new(supplier, id, room, new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc), price, new(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));

    private async Task<long> Scalar(string sql, object? p = null) =>
        await ScalarCore(sql, p);

    // The connection must be disposed: an undisposed SQLite connection keeps the database file open, so on Windows the test's
    // temp folder cannot be deleted afterwards (this leaked a folder per test run).
    private async Task<long> ScalarCore(string sql, object? p)
    {
        await using var conn = await _factory.OpenAsync();
        return await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, sql, p);
    }

    [Theory]
    [InlineData(1)]    // batching off: one commit per request
    [InlineData(8)]
    [InlineData(256)]
    public async Task Counters_and_rows_are_exact_under_heavy_concurrency(int maxBatch)
    {
        var batcher = NewBatcher(maxBatch);
        var repo = new ReservationRepository(_factory, batcher);
        var stats = new StatsRepository(_factory, batcher);

        // 5 suppliers x 40 distinct reservations, each sent 3 times (1 create + 2 identical duplicates),
        // plus 25 throttle events per supplier - all concurrently.
        var tasks = new List<Task>();
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<IngestOutcome>();
        for (var s = 0; s < 5; s++)
            for (var i = 0; i < 40; i++)
                for (var rep = 0; rep < 3; rep++)
                {
                    var (sup, id) = ($"sup{s}", $"r{i}");
                    tasks.Add(Task.Run(async () => outcomes.Add(await repo.UpsertAsync(R(sup, id)))));
                }
        for (var s = 0; s < 5; s++)
            for (var t = 0; t < 25; t++)
            {
                var sup = $"sup{s}";
                tasks.Add(Task.Run(() => stats.IncrementThrottledAsync(sup)));
            }
        await Task.WhenAll(tasks);

        outcomes.Count(o => o == IngestOutcome.Created).Should().Be(200);
        outcomes.Count(o => o == IngestOutcome.Unchanged).Should().Be(400);
        (await Scalar("SELECT COUNT(*) FROM Reservations")).Should().Be(200);

        for (var s = 0; s < 5; s++)
        {
            var st = await stats.GetAsync($"sup{s}");
            (st.Ingested, st.Unchanged, st.Throttled).Should().Be((40, 80, 25), $"supplier sup{s}"); // no lost or double counts
        }
    }

    [Fact]
    public async Task One_failing_item_does_not_affect_the_rest_of_its_batch_or_its_counters()
    {
        var batcher = NewBatcher();
        var repo = new ReservationRepository(_factory, batcher);

        var good = Enumerable.Range(0, 50).Select(i => Task.Run(() => repo.UpsertAsync(R("acme", $"ok{i}")))).ToList();
        // RoomId = null violates NOT NULL -> that single item must fault and roll back alone
        var bad = Task.Run(() => repo.UpsertAsync(R("acme", "bad") with { RoomId = null! }));

        var act = async () => await bad;
        await act.Should().ThrowAsync<Exception>();

        var results = await Task.WhenAll(good);
        results.Should().OnlyContain(o => o == IngestOutcome.Created);
        (await Scalar("SELECT COUNT(*) FROM Reservations")).Should().Be(50);
        (await Scalar("SELECT COUNT(*) FROM Reservations WHERE ReservationId='bad'")).Should().Be(0);
        (await Scalar("SELECT Ingested FROM SupplierStats WHERE SupplierId='acme'")).Should().Be(50); // failed item not counted
    }

    [Fact]
    public async Task Full_queue_rejects_with_StorageBusy_instead_of_growing_unbounded()
    {
        // Writer not started, so nothing drains: capacity 2 accepts 2 and rejects the 3rd synchronously.
        var batcher = NewBatcher(capacity: 2, start: false);
        var repo = new ReservationRepository(_factory, batcher);

        var first = repo.UpsertAsync(R("a", "1"));
        var second = repo.UpsertAsync(R("a", "2"));
        Action third = () => { _ = repo.UpsertAsync(R("a", "3")); };
        third.Should().Throw<StorageBusyException>();

        batcher.Start(); // now it drains: the two accepted ones complete normally
        (await first).Should().Be(IngestOutcome.Created);
        (await second).Should().Be(IngestOutcome.Created);
        (await Scalar("SELECT COUNT(*) FROM Reservations")).Should().Be(2);
    }

    [Fact]
    public async Task Shutdown_drains_everything_already_queued_and_then_rejects_new_work()
    {
        var batcher = NewBatcher(maxBatch: 4);
        var repo = new ReservationRepository(_factory, batcher);

        var pending = Enumerable.Range(0, 100).Select(i => repo.UpsertAsync(R("acme", $"r{i}"))).ToList(); // enqueued synchronously
        await batcher.StopAsync(CancellationToken.None);

        (await Task.WhenAll(pending)).Should().OnlyContain(o => o == IngestOutcome.Created);
        (await Scalar("SELECT COUNT(*) FROM Reservations")).Should().Be(100);

        Action late = () => { _ = repo.UpsertAsync(R("acme", "late")); };
        late.Should().Throw<StorageBusyException>();
    }

    [Fact]
    public async Task Callers_are_acknowledged_only_after_the_commit_is_visible_to_other_connections()
    {
        var batcher = NewBatcher();
        var repo = new ReservationRepository(_factory, batcher);

        for (var i = 0; i < 30; i++)
        {
            await repo.UpsertAsync(R("acme", $"r{i}"));
            // a brand-new connection must already see the row the moment the task completes
            (await Scalar("SELECT COUNT(*) FROM Reservations WHERE ReservationId=@id", new { id = $"r{i}" })).Should().Be(1);
        }
    }

    // ---- coalesced, awaited throttle counter -------------------------------------------------

    private async Task<long> Throttled(string supplier) =>
        await Scalar("SELECT COALESCE((SELECT Throttled FROM SupplierStats WHERE SupplierId=@supplier), 0)", new { supplier });

    [Fact]
    public async Task A_flood_of_throttles_for_one_supplier_takes_one_queue_slot_and_is_counted_exactly()
    {
        // Writer not started and capacity 2: before coalescing, the 3rd rejection would already overflow.
        var batcher = NewBatcher(capacity: 2, start: false);
        var stats = new StatsRepository(_factory, batcher);

        var all = Enumerable.Range(0, 500).Select(_ => stats.IncrementThrottledAsync("noisy")).ToList(); // none throws

        batcher.Start();
        await Task.WhenAll(all);

        (await Throttled("noisy")).Should().Be(500); // every rejection counted, none lost, none double-counted
    }

    [Fact]
    public async Task Each_supplier_gets_its_own_slot_so_the_queue_bound_still_applies_across_suppliers()
    {
        var batcher = NewBatcher(capacity: 2, start: false);
        var stats = new StatsRepository(_factory, batcher);

        var a = stats.IncrementThrottledAsync("a");
        var b = stats.IncrementThrottledAsync("b");
        Action c = () => { _ = stats.IncrementThrottledAsync("c"); };
        c.Should().Throw<StorageBusyException>(); // third distinct supplier does not fit

        batcher.Start();
        await Task.WhenAll(a, b);
        (await Throttled("a")).Should().Be(1);
        (await Throttled("b")).Should().Be(1);
        (await Throttled("c")).Should().Be(0);
    }

    [Fact]
    public async Task A_failed_enqueue_leaves_nothing_stuck_and_the_supplier_works_again_later()
    {
        var batcher = NewBatcher(capacity: 1, start: false);
        var repo = new ReservationRepository(_factory, batcher);
        var stats = new StatsRepository(_factory, batcher);

        var upsert = repo.UpsertAsync(R("x", "1"));                 // fills the single slot
        Action first = () => { _ = stats.IncrementThrottledAsync("noisy"); };
        Action second = () => { _ = stats.IncrementThrottledAsync("noisy"); };
        first.Should().Throw<StorageBusyException>();
        second.Should().Throw<StorageBusyException>();              // not wedged on a stale pending item

        batcher.Start();
        await upsert;
        await stats.IncrementThrottledAsync("noisy");               // queue has room again
        (await Throttled("noisy")).Should().Be(1);
    }

    [Fact]
    public async Task A_throttle_flood_does_not_starve_real_writes_of_other_suppliers()
    {
        var batcher = NewBatcher(capacity: 50);                     // small bound, writer running
        var repo = new ReservationRepository(_factory, batcher);
        var stats = new StatsRepository(_factory, batcher);

        var flood = Enumerable.Range(0, 5000).Select(_ => Task.Run(() => stats.IncrementThrottledAsync("noisy"))).ToList();
        var real = Enumerable.Range(0, 30).Select(i => Task.Run(() => repo.UpsertAsync(R("calm", $"r{i}")))).ToList();

        (await Task.WhenAll(real)).Should().OnlyContain(o => o == IngestOutcome.Created); // no 503 for the calm supplier
        await Task.WhenAll(flood);

        (await Throttled("noisy")).Should().Be(5000);
        (await Scalar("SELECT COUNT(*) FROM Reservations WHERE SupplierId='calm'")).Should().Be(30);
    }

    [Fact]
    public async Task A_throttle_caller_is_acknowledged_only_after_its_count_is_committed()
    {
        var batcher = NewBatcher();
        var stats = new StatsRepository(_factory, batcher);

        for (var i = 1; i <= 25; i++)
        {
            await stats.IncrementThrottledAsync("acme");
            // sequential awaits cannot coalesce, so a fresh connection must already see exactly i
            (await Throttled("acme")).Should().Be(i);
        }
    }

    [Fact]
    public async Task Concurrent_throttles_across_many_suppliers_count_exactly_with_coalescing()
    {
        var batcher = NewBatcher();
        var stats = new StatsRepository(_factory, batcher);

        await Task.WhenAll(Enumerable.Range(0, 8 * 300).Select(i =>
            Task.Run(() => stats.IncrementThrottledAsync($"s{i % 8}"))));

        for (var s = 0; s < 8; s++) (await Throttled($"s{s}")).Should().Be(300);
    }
}

