using FluentAssertions;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Xunit;

namespace Mize.Reservations.Contracts;

/// <summary>
/// Behavioural contract every <see cref="IReservationArchive"/> must satisfy. Derive in an adapter's test project and
/// implement <see cref="CreateAsync"/> (a fresh, empty archive per test).
/// </summary>
public abstract class ArchiveContract : IAsyncLifetime
{
    private IReservationArchive _archive = null!;
    private Func<Task>? _cleanup;

    /// <summary>A fresh, empty archive and an optional cleanup.</summary>
    protected abstract Task<(IReservationArchive Archive, Func<Task>? Cleanup)> CreateAsync();

    /// <summary>
    /// When non-null, every test is reported as skipped with this reason instead of running (for adapters that need an
    /// external server, for example S3). Nothing is created in that case.
    /// </summary>
    protected virtual string? SkipReason => null;

    public async Task InitializeAsync()
    {
        Guard();
        if (SkipReason is null) (_archive, _cleanup) = await CreateAsync();
    }

    private void Guard() => Skip.If(SkipReason is not null, SkipReason);
    public async Task DisposeAsync() { if (_cleanup is not null) await _cleanup(); }

    private static Reservation R(string supplier, string id, string room = "room1", long price = 45000, int day = 1) =>
        new(supplier, id, room, new(2024, 8, 1, 0, 0, 0, DateTimeKind.Utc), new(2024, 8, 3, 0, 0, 0, DateTimeKind.Utc),
            price, new DateTime(2024, 7, day, 10, 15, 0, 123, DateTimeKind.Utc).AddTicks(4567));

    private static ArchiveBatch Batch(params Reservation[] rows) => ArchiveLayout.Partition(rows).Single();

    [SkippableFact]
    public async Task What_is_written_is_read_back_exactly()
    {
        Guard();
        var rows = new[] { R("acme", "r1"), R("acme", "r2", price: 1), R("acme", "r3", room: "Zimmer Ä/ö 日本") };
        var batch = Batch(rows);

        await _archive.WriteAsync(batch);
        var back = await _archive.ReadAsync(batch.Key);

        back.Should().Equal(batch.Reservations); // same rows, same order, full precision
        back.Should().OnlyContain(r => r.UpdatedAtUtc.Kind == DateTimeKind.Utc && r.CheckInUtc.Kind == DateTimeKind.Utc);
    }

    [SkippableFact]
    public async Task Writing_the_same_batch_again_is_idempotent()
    {
        Guard();
        var batch = Batch(R("acme", "r1"), R("acme", "r2"));

        await _archive.WriteAsync(batch);
        await _archive.WriteAsync(batch); // a retry after a crash

        (await _archive.ListAsync(ArchiveLayout.Root)).Should().Equal(batch.Key); // still one object
        (await _archive.ReadAsync(batch.Key)).Should().Equal(batch.Reservations);
    }

    [SkippableFact]
    public async Task Different_batches_are_independent_and_listed_by_prefix_in_order()
    {
        Guard();
        var a = Batch(R("acme", "r1"));
        var b = Batch(R("other", "r1"));
        await _archive.WriteAsync(a);
        await _archive.WriteAsync(b);

        var all = await _archive.ListAsync(ArchiveLayout.Root);
        all.Should().BeEquivalentTo(new[] { a.Key, b.Key }).And.BeInAscendingOrder(StringComparer.Ordinal);

        (await _archive.ListAsync($"{ArchiveLayout.Root}checkout=2024-08/supplier=acme/")).Should().Equal(a.Key);
        (await _archive.ListAsync($"{ArchiveLayout.Root}checkout=1999-01/")).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task A_large_batch_round_trips()
    {
        Guard();
        var rows = Enumerable.Range(0, 5000).Select(i => R("acme", $"r{i:D5}", price: i)).ToArray();
        var batch = Batch(rows);

        await _archive.WriteAsync(batch);

        (await _archive.ReadAsync(batch.Key)).Should().Equal(batch.Reservations);
    }

    [SkippableTheory]
    [InlineData("../../escape")]
    [InlineData("a/b/c")]
    [InlineData("..")]
    [InlineData("UPPER Case")]
    [InlineData("tab\tand space")]
    [InlineData("日本語")]
    [InlineData("100%")]
    public async Task Hostile_or_unusual_supplier_ids_stay_inside_the_archive_and_round_trip(string supplier)
    {
        Guard();
        var batch = Batch(R(supplier, "r1"));

        await _archive.WriteAsync(batch);

        batch.Key.Should().StartWith(ArchiveLayout.Root).And.NotContain("..");
        (await _archive.ListAsync(ArchiveLayout.Root)).Should().Equal(batch.Key);
        (await _archive.ReadAsync(batch.Key)).Single().SupplierId.Should().Be(supplier);
    }

    [SkippableFact]
    public async Task Reading_a_missing_key_throws_KeyNotFound()
    {
        Guard();
        var act = async () => await _archive.ReadAsync($"{ArchiveLayout.Root}checkout=2024-01/supplier=none/missing.jsonl.gz");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [SkippableFact]
    public async Task A_key_can_never_resolve_outside_the_archive()
    {
        Guard();
        var evil = new ArchiveBatch("../../outside.jsonl.gz", [R("acme", "r1")]);

        var act = async () => await _archive.WriteAsync(evil);

        await act.Should().ThrowAsync<Exception>();
        (await _archive.ListAsync("")).Should().BeEmpty();
    }
}
