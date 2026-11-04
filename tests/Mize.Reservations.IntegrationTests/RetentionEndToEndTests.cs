using Dapper;
using FluentAssertions;
using Mize.Reservations.Archive.FileSystem;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Mize.Reservations.Storage.Sqlite;
using Xunit;

namespace Mize.Reservations.IntegrationTests;

internal sealed class FixedClock(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
}

internal sealed class AlwaysAllow : IRateLimiter
{
    public Task<RateLimitDecision> TryAcquireAsync(string supplierKey, CancellationToken ct = default) =>
        Task.FromResult(new RateLimitDecision(true, TimeSpan.Zero));
}

/// <summary>Wraps the real store to inject the failures retention must survive.</summary>
internal sealed class FaultyStore(IReservationRetentionStore inner) : IReservationRetentionStore
{
    public Func<IReadOnlyList<Reservation>, Task>? BeforeDelete { get; set; }
    public bool FailNextDelete { get; set; }

    public Task<IReadOnlyList<Reservation>> GetExpiredAsync(DateTime cutoffUtc, int limit, Reservation? after = null, CancellationToken ct = default) =>
        inner.GetExpiredAsync(cutoffUtc, limit, after, ct);

    public async Task<int> DeleteIfUnchangedAsync(IReadOnlyList<Reservation> archived, CancellationToken ct = default)
    {
        if (BeforeDelete is not null) await BeforeDelete(archived);
        if (FailNextDelete) { FailNextDelete = false; throw new IOException("simulated crash after archiving"); }
        return await inner.DeleteIfUnchangedAsync(archived, ct);
    }

    public Task ReclaimSpaceAsync(CancellationToken ct = default) => inner.ReclaimSpaceAsync(ct);
}

internal sealed class FailingArchive : IReservationArchive
{
    public Task WriteAsync(ArchiveBatch batch, CancellationToken ct = default) => throw new IOException("archive unavailable");
    public Task<IReadOnlyList<Reservation>> ReadAsync(string key, CancellationToken ct = default) => throw new IOException("archive unavailable");
    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default) => throw new IOException("archive unavailable");
}

/// <summary>Real SQLite (with the group-commit writer) + the real directory archive + the real RetentionService.</summary>
internal sealed class RetentionHarness : IAsyncDisposable
{
    public static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);          // cutoff with 365 days: 2025-06-01
    public static readonly DateTime OldStay = new(2024, 3, 10, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime RecentStay = new(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-retention-" + Guid.NewGuid().ToString("N"));

    public SqliteConnectionFactory Factory { get; private set; } = null!;
    public WriteBatcher Batcher { get; private set; } = null!;
    public ReservationRepository Repo { get; private set; } = null!;
    public StatsRepository Stats { get; private set; } = null!;
    public SqliteRetentionStore Store { get; private set; } = null!;
    public FileSystemReservationArchive Archive { get; private set; } = null!;
    public RetentionPolicy Policy { get; private set; } = null!;
    public string ArchiveDir => Path.Combine(_dir, "archive");

    public static async Task<RetentionHarness> CreateAsync(RetentionPolicy? policy = null)
    {
        var h = new RetentionHarness { Policy = policy ?? new RetentionPolicy(Enabled: true, RetentionDays: 365, BatchSize: 1000) };
        h.Factory = new SqliteConnectionFactory(Path.Combine(h._dir, "t.db"));
        await new SqliteStorageInitializer(h.Factory).InitializeAsync();
        h.Batcher = new WriteBatcher(h.Factory, new WriteBatcherOptions());
        h.Batcher.Start();
        h.Repo = new ReservationRepository(h.Factory, h.Batcher);
        h.Stats = new StatsRepository(h.Factory, h.Batcher);
        h.Store = new SqliteRetentionStore(h.Factory, h.Batcher);
        h.Archive = new FileSystemReservationArchive(h.ArchiveDir);
        return h;
    }

    public RetentionService Service(IReservationRetentionStore? store = null, IReservationArchive? archive = null) =>
        new(store ?? Store, archive ?? Archive, Policy, new FixedClock(Now));

    public static Reservation R(string supplier, string id, DateTime checkOut, long price = 10000, int updatedDay = 1) =>
        new(supplier, id, "room1", checkOut.AddDays(-2), checkOut, price, new DateTime(2024, 3, updatedDay, 9, 0, 0, DateTimeKind.Utc));

    public async Task Seed(params Reservation[] rows)
    {
        // chunked: the write queue is deliberately bounded (default 2000), so bulk loads must not exceed it at once
        foreach (var chunk in rows.Chunk(500))
            await Task.WhenAll(chunk.Select(r => Repo.UpsertAsync(r)));
    }

    public async Task<long> RowCount()
    {
        await using var c = await Factory.OpenAsync();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Reservations");
    }

    public async Task<List<Reservation>> EverythingInArchive()
    {
        var all = new List<Reservation>();
        foreach (var key in await Archive.ListAsync(ArchiveLayout.Root)) all.AddRange(await Archive.ReadAsync(key));
        return all;
    }

    public async ValueTask DisposeAsync()
    {
        await Batcher.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}

public sealed class RetentionEndToEndTests
{
    private static Reservation R(string supplier, string id, DateTime checkOut, long price = 10000, int updatedDay = 1) =>
        RetentionHarness.R(supplier, id, checkOut, price, updatedDay);

    [Fact]
    public async Task Old_reservations_move_to_the_archive_and_leave_the_database_while_recent_ones_stay()
    {
        await using var h = await RetentionHarness.CreateAsync();
        var old = new[] { R("acme", "o1", RetentionHarness.OldStay), R("acme", "o2", RetentionHarness.OldStay), R("other", "o3", RetentionHarness.OldStay) };
        var recent = new[] { R("acme", "n1", RetentionHarness.RecentStay), R("other", "n2", RetentionHarness.RecentStay) };
        await h.Seed(old); await h.Seed(recent);
        var statsBefore = await h.Stats.GetAsync("acme");

        var result = await h.Service().RunOnceAsync();

        result.Archived.Should().Be(3);
        result.Deleted.Should().Be(3);
        (await h.RowCount()).Should().Be(2);
        (await h.Repo.GetAsync("acme", "n1")).Should().NotBeNull();
        (await h.Repo.GetAsync("acme", "o1")).Should().BeNull();
        (await h.EverythingInArchive()).Should().BeEquivalentTo(old);                 // archived exactly what was removed
        (await h.Stats.GetAsync("acme")).Should().Be(statsBefore);                    // lifetime counters untouched

        // idempotent: a second run has nothing to do and writes nothing new
        var objectsAfterFirst = await h.Archive.ListAsync(ArchiveLayout.Root);
        var again = await h.Service().RunOnceAsync();
        (again.Archived, again.Deleted).Should().Be((0, 0));
        (await h.Archive.ListAsync(ArchiveLayout.Root)).Should().Equal(objectsAfterFirst);
    }

    [Fact]
    public async Task A_crash_after_archiving_loses_nothing_and_the_rerun_finishes_the_job_without_duplicate_objects()
    {
        await using var h = await RetentionHarness.CreateAsync();
        var old = Enumerable.Range(0, 20).Select(i => R(i % 2 == 0 ? "acme" : "other", $"o{i:D2}", RetentionHarness.OldStay)).ToArray();
        await h.Seed(old);
        var faulty = new FaultyStore(h.Store) { FailNextDelete = true };

        var crash = async () => await h.Service(store: faulty).RunOnceAsync();
        await crash.Should().ThrowAsync<IOException>();

        (await h.RowCount()).Should().Be(20);                                         // nothing deleted
        var keys = await h.Archive.ListAsync(ArchiveLayout.Root);
        keys.Should().NotBeEmpty();                                                   // but already archived

        var rerun = await h.Service(store: faulty).RunOnceAsync();

        rerun.Deleted.Should().Be(20);
        (await h.RowCount()).Should().Be(0);
        (await h.Archive.ListAsync(ArchiveLayout.Root)).Should().Equal(keys);         // same objects overwritten, none added
        (await h.EverythingInArchive()).Should().BeEquivalentTo(old);                 // every reservation archived exactly once
    }

    [Fact]
    public async Task A_supplier_update_between_archive_and_delete_keeps_the_live_reservation()
    {
        await using var h = await RetentionHarness.CreateAsync();
        await h.Seed(R("acme", "stays-live", RetentionHarness.OldStay, price: 10000), R("acme", "goes", RetentionHarness.OldStay));
        var faulty = new FaultyStore(h.Store)
        {
            // the supplier moves the stay to a future date and changes the price, after the pass archived the old version
            BeforeDelete = _ => h.Repo.UpsertAsync(R("acme", "stays-live", RetentionHarness.RecentStay, price: 99999, updatedDay: 20))
        };

        var result = await h.Service(store: faulty).RunOnceAsync();

        result.Deleted.Should().Be(1);
        result.SkippedBecauseChanged.Should().Be(1);
        var live = await h.Repo.GetAsync("acme", "stays-live");
        live.Should().NotBeNull("a live, updated reservation must never be deleted by a stale archive pass");
        live!.PriceCents.Should().Be(99999);
        (await h.Repo.GetAsync("acme", "goes")).Should().BeNull();
    }

    [Fact]
    public async Task When_the_archive_is_unavailable_nothing_is_deleted()
    {
        await using var h = await RetentionHarness.CreateAsync();
        await h.Seed(R("acme", "o1", RetentionHarness.OldStay), R("acme", "o2", RetentionHarness.OldStay));

        var act = async () => await h.Service(archive: new FailingArchive()).RunOnceAsync();

        await act.Should().ThrowAsync<IOException>();
        (await h.RowCount()).Should().Be(2);
    }

    [Fact]
    public async Task After_a_purge_a_late_update_for_the_purged_reservation_is_ignored_not_resurrected()
    {
        await using var h = await RetentionHarness.CreateAsync();
        await h.Seed(R("acme", "old-one", RetentionHarness.OldStay));
        await h.Service().RunOnceAsync();
        (await h.Repo.GetAsync("acme", "old-one")).Should().BeNull();                 // purged

        var ingest = new IngestService(new AlwaysAllow(), h.Repo, h.Stats, h.Policy, new FixedClock(RetentionHarness.Now));
        IngestCommand Cmd(string id, DateTime checkOut) => new("acme", id, "room1",
            new DateTimeOffset(checkOut.AddDays(-2), TimeSpan.Zero), new DateTimeOffset(checkOut, TimeSpan.Zero), 450m,
            new DateTimeOffset(RetentionHarness.Now, TimeSpan.Zero));

        // the supplier retries an old update: without the guard this would be stored as a brand-new reservation
        var late = await ingest.IngestAsync(Cmd("old-one", RetentionHarness.OldStay));

        late.Outcome.Should().Be(IngestOutcome.Expired);
        (await h.Repo.GetAsync("acme", "old-one")).Should().BeNull();
        (await ingest.IngestAsync(Cmd("fresh", RetentionHarness.RecentStay))).Outcome.Should().Be(IngestOutcome.Created); // normal traffic unaffected
    }

    [Fact]
    public async Task Thousands_of_rows_move_in_batches()
    {
        await using var h = await RetentionHarness.CreateAsync(new RetentionPolicy(Enabled: true, RetentionDays: 365, BatchSize: 500));
        var old = Enumerable.Range(0, 5000).Select(i => R(i % 7 == 0 ? "acme" : "other", $"o{i:D5}", RetentionHarness.OldStay.AddDays(i % 40))).ToArray();
        await h.Seed(old);

        var result = await h.Service().RunOnceAsync();

        result.Batches.Should().Be(10);
        result.Deleted.Should().Be(5000);
        (await h.RowCount()).Should().Be(0);

        // ordered Equal, not BeEquivalentTo: a deep structural comparison of 5,000 objects is quadratic and effectively hangs
        static IEnumerable<Reservation> ByKey(IEnumerable<Reservation> rows) =>
            rows.OrderBy(r => r.SupplierId, StringComparer.Ordinal).ThenBy(r => r.ReservationId, StringComparer.Ordinal);
        ByKey(await h.EverythingInArchive()).Should().Equal(ByKey(old));
    }

    [Fact]
    public async Task A_dry_run_changes_nothing()
    {
        await using var h = await RetentionHarness.CreateAsync(new RetentionPolicy(Enabled: true, RetentionDays: 365, DryRun: true));
        await h.Seed(R("acme", "o1", RetentionHarness.OldStay), R("acme", "o2", RetentionHarness.OldStay));

        var result = await h.Service().RunOnceAsync();

        result.Archived.Should().Be(2);
        result.Deleted.Should().Be(0);
        (await h.RowCount()).Should().Be(2);
        (await h.Archive.ListAsync("")).Should().BeEmpty();
    }
}

/// <summary>SQLite-specific behaviour behind the retention port.</summary>
public sealed class SqliteRetentionSpecificTests
{
    private static async Task<long> Scalar(SqliteConnectionFactory f, string sql)
    {
        await using var c = await f.OpenAsync();
        return await c.ExecuteScalarAsync<long>(sql);
    }

    [Fact]
    public async Task A_new_database_is_created_with_incremental_auto_vacuum()
    {
        await using var h = await RetentionHarness.CreateAsync();
        (await Scalar(h.Factory, "PRAGMA auto_vacuum;")).Should().Be(2); // 2 = INCREMENTAL
    }

    [Fact]
    public async Task The_retention_index_is_created_on_first_use_not_at_startup()
    {
        await using var h = await RetentionHarness.CreateAsync();
        const string exists = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_Reservations_Retention'";

        (await Scalar(h.Factory, exists)).Should().Be(0, "deployments that never enable retention must not pay for the index on every insert");

        await h.Store.GetExpiredAsync(RetentionHarness.Now, 10);

        (await Scalar(h.Factory, exists)).Should().Be(1);
    }

    [Fact]
    public async Task Reclaiming_returns_the_pages_freed_by_deletes_to_the_file_system()
    {
        await using var h = await RetentionHarness.CreateAsync(new RetentionPolicy(Enabled: true, RetentionDays: 365, BatchSize: 2000));
        await h.Seed(Enumerable.Range(0, 4000).Select(i => RetentionHarness.R("acme", $"o{i:D5}", RetentionHarness.OldStay)).ToArray());

        var page = await h.Store.GetExpiredAsync(RetentionHarness.Now.AddDays(-365), 5000);
        await h.Store.DeleteIfUnchangedAsync(page);
        (await Scalar(h.Factory, "PRAGMA freelist_count;")).Should().BeGreaterThan(0); // deleted rows left free pages behind

        await h.Store.ReclaimSpaceAsync();

        (await Scalar(h.Factory, "PRAGMA freelist_count;")).Should().Be(0);
    }

    [Fact]
    public async Task The_expired_query_uses_the_covering_index_not_a_table_scan()
    {
        await using var h = await RetentionHarness.CreateAsync();
        await h.Store.GetExpiredAsync(RetentionHarness.Now, 1); // creates the index

        await using var c = await h.Factory.OpenAsync();
        var plan = (await c.QueryAsync<(int id, int parent, int notused, string detail)>(
            "EXPLAIN QUERY PLAN SELECT SupplierId FROM Reservations WHERE CheckOut < '2025-06-01' ORDER BY CheckOut, SupplierId, ReservationId LIMIT 10"))
            .Select(r => r.detail).ToList();

        plan.Should().Contain(d => d.Contains("IX_Reservations_Retention"));
        plan.Should().NotContain(d => d.Contains("USE TEMP B-TREE")); // ordered by the index itself: no sort
    }
}
