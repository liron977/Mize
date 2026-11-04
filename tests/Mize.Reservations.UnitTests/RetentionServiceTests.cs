using FluentAssertions;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Xunit;

namespace Mize.Reservations.UnitTests;

internal sealed class FakeRetentionStore(List<string> log) : IReservationRetentionStore
{
    public List<Reservation> Rows { get; } = new();
    public Action<IReadOnlyList<Reservation>>? BeforeDelete { get; set; }
    public bool FailNextDelete { get; set; }
    public int Reclaims { get; private set; }

    public Task<IReadOnlyList<Reservation>> GetExpiredAsync(DateTime cutoffUtc, int limit, Reservation? after = null, CancellationToken ct = default)
    {
        int Cmp(Reservation a, Reservation b)
        {
            var c = a.CheckOutUtc.CompareTo(b.CheckOutUtc);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.SupplierId, b.SupplierId);
            return c != 0 ? c : string.CompareOrdinal(a.ReservationId, b.ReservationId);
        }

        log.Add("read");
        IReadOnlyList<Reservation> page = Rows.Where(r => r.CheckOutUtc < cutoffUtc && (after is null || Cmp(r, after) > 0))
            .OrderBy(r => r, Comparer<Reservation>.Create(Cmp)).Take(limit).ToList();
        return Task.FromResult(page);
    }

    public Task<int> DeleteIfUnchangedAsync(IReadOnlyList<Reservation> archived, CancellationToken ct = default)
    {
        BeforeDelete?.Invoke(archived);
        if (FailNextDelete) { FailNextDelete = false; throw new IOException("simulated crash after archiving"); }

        log.Add("delete");
        var deleted = 0;
        foreach (var r in archived)
        {
            var current = Rows.FindIndex(x => x.SupplierId == r.SupplierId && x.ReservationId == r.ReservationId);
            if (current >= 0 && Rows[current] == r) { Rows.RemoveAt(current); deleted++; }
        }
        return Task.FromResult(deleted);
    }

    public Task ReclaimSpaceAsync(CancellationToken ct = default) { Reclaims++; return Task.CompletedTask; }
}

internal sealed class RecordingArchive(List<string> log) : IReservationArchive
{
    public Dictionary<string, IReadOnlyList<Reservation>> Objects { get; } = new();
    public int Writes { get; private set; }
    public bool FailWrites { get; set; }

    public Task WriteAsync(ArchiveBatch batch, CancellationToken ct = default)
    {
        if (FailWrites) throw new IOException("archive unavailable");
        log.Add("write");
        Writes++;
        Objects[batch.Key] = batch.Reservations; // same key twice replaces: idempotent, like the real adapters
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Reservation>> ReadAsync(string key, CancellationToken ct = default) =>
        Objects.TryGetValue(key, out var v) ? Task.FromResult(v) : throw new KeyNotFoundException(key);

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(k => k.StartsWith(prefix)).OrderBy(k => k, StringComparer.Ordinal).ToList());
}

public class RetentionServiceTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = new(2024, 3, 10, 0, 0, 0, DateTimeKind.Utc); // long before the cutoff

    private static Reservation R(string supplier, string id, DateTime? checkOut = null, long price = 100, int updatedDay = 1)
    {
        var co = checkOut ?? Old;
        return new(supplier, id, "room", co.AddDays(-2), co, price, new DateTime(2024, 3, updatedDay, 9, 0, 0, DateTimeKind.Utc));
    }

    private static (RetentionService svc, FakeRetentionStore store, RecordingArchive archive, List<string> log) Build(
        RetentionPolicy? policy = null)
    {
        var log = new List<string>();
        var store = new FakeRetentionStore(log);
        var archive = new RecordingArchive(log);
        var svc = new RetentionService(store, archive, policy ?? new RetentionPolicy(Enabled: true, RetentionDays: 365, BatchSize: 1000),
            new FixedUtcTimeProvider(Now));
        return (svc, store, archive, log);
    }

    [Fact]
    public async Task Archives_first_then_deletes_and_reports_exact_counts()
    {
        var (svc, store, archive, log) = Build();
        store.Rows.AddRange([R("acme", "r1"), R("acme", "r2"), R("other", "r1")]);
        store.Rows.Add(R("acme", "recent", checkOut: Now.AddDays(-10))); // inside the window: untouched

        var result = await svc.RunOnceAsync();

        result.Archived.Should().Be(3);
        result.Deleted.Should().Be(3);
        result.SkippedBecauseChanged.Should().Be(0);
        result.ArchiveObjects.Should().Be(2); // acme/2024-03 and other/2024-03
        store.Rows.Select(r => r.ReservationId).Should().Equal("recent");
        archive.Objects.Values.SelectMany(v => v).Should().HaveCount(3);

        // every write happens before the delete: a row is never deleted before its archive copy exists
        log.Where(e => e is "write" or "delete").Should().Equal("write", "write", "delete");
        store.Reclaims.Should().Be(1);
    }

    [Fact]
    public async Task Nothing_is_deleted_when_the_archive_write_fails()
    {
        var (svc, store, archive, _) = Build();
        store.Rows.AddRange([R("acme", "r1"), R("acme", "r2")]);
        archive.FailWrites = true;

        var act = async () => await svc.RunOnceAsync();

        await act.Should().ThrowAsync<IOException>();
        store.Rows.Should().HaveCount(2); // all still there: no data loss
    }

    [Fact]
    public async Task A_crash_between_archive_and_delete_loses_nothing_and_the_rerun_creates_no_duplicate_objects()
    {
        var (svc, store, archive, _) = Build();
        store.Rows.AddRange([R("acme", "r1"), R("acme", "r2"), R("other", "r1")]);
        store.FailNextDelete = true;

        var crashed = async () => await svc.RunOnceAsync();
        await crashed.Should().ThrowAsync<IOException>();
        store.Rows.Should().HaveCount(3);                   // archived but not yet deleted
        var keysAfterCrash = archive.Objects.Keys.OrderBy(k => k).ToList();

        var rerun = await svc.RunOnceAsync();

        rerun.Deleted.Should().Be(3);
        store.Rows.Should().BeEmpty();
        archive.Objects.Keys.OrderBy(k => k).Should().Equal(keysAfterCrash); // same keys overwritten, not new ones
        archive.Objects.Values.SelectMany(v => v).Should().HaveCount(3);     // each reservation archived once
    }

    [Fact]
    public async Task A_row_changed_by_a_supplier_between_archive_and_delete_is_kept_not_deleted()
    {
        var (svc, store, archive, _) = Build();
        store.Rows.AddRange([R("acme", "stays-live"), R("acme", "goes")]);
        store.BeforeDelete = _ =>
        {
            // supplier updates the reservation (new price, newer timestamp) after it was archived
            var i = store.Rows.FindIndex(r => r.ReservationId == "stays-live");
            store.Rows[i] = store.Rows[i] with { PriceCents = 999_999, UpdatedAtUtc = Now };
        };

        var result = await svc.RunOnceAsync();

        result.Deleted.Should().Be(1);
        result.SkippedBecauseChanged.Should().Be(1);
        store.Rows.Should().ContainSingle(r => r.ReservationId == "stays-live" && r.PriceCents == 999_999);
        archive.Objects.Values.SelectMany(v => v).Should().Contain(r => r.ReservationId == "stays-live"); // archived copy is the old version
    }

    [Fact]
    public async Task Pages_through_everything_in_batches_visiting_each_row_once()
    {
        var (svc, store, archive, log) = Build(new RetentionPolicy(Enabled: true, RetentionDays: 365, BatchSize: 3));
        store.Rows.AddRange(Enumerable.Range(0, 10).Select(i => R("acme", $"r{i:D2}")));

        var result = await svc.RunOnceAsync();

        result.Batches.Should().Be(4);              // 3 + 3 + 3 + 1
        result.Archived.Should().Be(10);
        result.Deleted.Should().Be(10);
        archive.Objects.Values.SelectMany(v => v).Select(r => r.ReservationId).Should().OnlyHaveUniqueItems().And.HaveCount(10);
        log.Count(e => e == "delete").Should().Be(4);
    }

    [Fact]
    public async Task MaxBatchesPerRun_bounds_a_single_run_and_the_rest_waits_for_the_next()
    {
        var (svc, store, _, _) = Build(new RetentionPolicy(Enabled: true, RetentionDays: 365, BatchSize: 3, MaxBatchesPerRun: 2));
        store.Rows.AddRange(Enumerable.Range(0, 10).Select(i => R("acme", $"r{i:D2}")));

        var first = await svc.RunOnceAsync();
        first.Batches.Should().Be(2);
        first.Deleted.Should().Be(6);
        store.Rows.Should().HaveCount(4);

        (await svc.RunOnceAsync()).Deleted.Should().Be(4); // 3 + 1, within the cap
        store.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Dry_run_pages_through_everything_but_writes_and_deletes_nothing()
    {
        var (svc, store, archive, log) = Build(new RetentionPolicy(Enabled: true, RetentionDays: 365, BatchSize: 3, DryRun: true));
        store.Rows.AddRange(Enumerable.Range(0, 10).Select(i => R("acme", $"r{i:D2}")));

        var result = await svc.RunOnceAsync();

        result.DryRun.Should().BeTrue();
        result.Archived.Should().Be(10);            // "would archive"
        result.Deleted.Should().Be(0);
        result.ArchiveObjects.Should().Be(0);
        archive.Writes.Should().Be(0);
        store.Rows.Should().HaveCount(10);
        log.Should().NotContain("delete").And.NotContain("write");
        store.Reclaims.Should().Be(0);
    }

    [Fact]
    public async Task A_disabled_policy_does_nothing_at_all()
    {
        var (svc, store, archive, log) = Build(RetentionPolicy.Disabled);
        store.Rows.Add(R("acme", "r1"));

        var result = await svc.RunOnceAsync();

        result.Archived.Should().Be(0);
        log.Should().BeEmpty();                      // never even reads
        archive.Writes.Should().Be(0);
        store.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task When_nothing_is_expired_nothing_is_archived_or_reclaimed()
    {
        var (svc, store, archive, _) = Build();
        store.Rows.Add(R("acme", "recent", checkOut: Now.AddDays(-5)));

        var result = await svc.RunOnceAsync();

        (result.Batches, result.Archived, result.Deleted).Should().Be((0, 0, 0));
        archive.Writes.Should().Be(0);
        store.Reclaims.Should().Be(0);
    }

    [Fact]
    public async Task The_cutoff_comes_from_the_injected_clock_and_the_retention_days()
    {
        var (svc, store, _, _) = Build(new RetentionPolicy(Enabled: true, RetentionDays: 100));
        var cutoff = Now.AddDays(-100);
        store.Rows.AddRange([R("a", "just-expired", checkOut: cutoff.AddSeconds(-1)), R("a", "on-cutoff", checkOut: cutoff), R("a", "inside", checkOut: cutoff.AddDays(1))]);

        var result = await svc.RunOnceAsync();

        result.CutoffUtc.Should().Be(cutoff);
        store.Rows.Select(r => r.ReservationId).Should().BeEquivalentTo("on-cutoff", "inside");
    }

    [Fact]
    public async Task One_page_is_split_into_one_archive_object_per_month_and_supplier()
    {
        var (svc, store, archive, _) = Build();
        store.Rows.AddRange([
            R("acme", "a1", new DateTime(2024, 3, 10, 0, 0, 0, DateTimeKind.Utc)),
            R("acme", "a2", new DateTime(2024, 4, 10, 0, 0, 0, DateTimeKind.Utc)),
            R("other", "o1", new DateTime(2024, 3, 11, 0, 0, 0, DateTimeKind.Utc))]);

        await svc.RunOnceAsync();

        archive.Objects.Keys.Select(k => k[ArchiveLayout.Root.Length..k.LastIndexOf('/')]).Should().BeEquivalentTo(
            "checkout=2024-03/supplier=acme", "checkout=2024-04/supplier=acme", "checkout=2024-03/supplier=other");
    }
}
