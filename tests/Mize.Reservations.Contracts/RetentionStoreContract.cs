using FluentAssertions;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Xunit;

namespace Mize.Reservations.Contracts;

/// <summary>
/// Behavioural contract for <see cref="IReservationRetentionStore"/>: how expired rows are read (cutoff, order, paging) and,
/// above all, that a delete only ever removes the exact version that was archived. Derive in an engine's test project,
/// return a <see cref="StorageUnderTest"/> that has its <c>Retention</c> set, and xUnit runs everything below against it.
/// </summary>
public abstract class RetentionStoreContract : IAsyncLifetime
{
    private StorageUnderTest _s = null!;

    protected abstract Task<StorageUnderTest> CreateAsync();

    private IReservationRepository Repo => _s.Reservations;
    private IReservationRetentionStore Store => _s.Retention ?? throw new InvalidOperationException("StorageUnderTest.Retention is required.");

    public async Task InitializeAsync() => _s = await CreateAsync();
    public async Task DisposeAsync() => await _s.DisposeAsync();

    private static readonly DateTime Cutoff = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime Day(int days) => Cutoff.AddDays(days); // negative = before the cutoff (expired)

    private static Reservation R(string supplier, string id, DateTime checkOut, long price = 10000, int updatedDay = 1) =>
        new(supplier, id, "room1", checkOut.AddDays(-2), checkOut, price, new DateTime(2024, 7, updatedDay, 10, 0, 0, DateTimeKind.Utc));

    private async Task Seed(params Reservation[] rows)
    {
        foreach (var r in rows) await Repo.UpsertAsync(r);
    }

    private async Task<List<Reservation>> AllExpired(int pageSize)
    {
        var all = new List<Reservation>();
        Reservation? cursor = null;
        while (true)
        {
            var page = await Store.GetExpiredAsync(Cutoff, pageSize, cursor);
            if (page.Count == 0) return all;
            all.AddRange(page);
            cursor = page[^1];
        }
    }

    [Fact]
    public async Task Returns_only_rows_whose_checkout_is_strictly_before_the_cutoff()
    {
        await Seed(R("a", "old", Day(-1)), R("a", "on-the-cutoff", Day(0)), R("a", "future", Day(30)));

        var expired = await Store.GetExpiredAsync(Cutoff, 100);

        expired.Select(r => r.ReservationId).Should().Equal("old"); // equal to the cutoff is NOT expired
    }

    [Fact]
    public async Task Returns_the_complete_row()
    {
        var original = R("acme", "r1", Day(-10), price: 12345, updatedDay: 7);
        await Seed(original);

        var row = (await Store.GetExpiredAsync(Cutoff, 10)).Single();

        row.Should().Be(original);
        row.CheckOutUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Orders_by_checkout_then_supplier_then_reservation_id()
    {
        await Seed(
            R("b", "2", Day(-5)), R("a", "9", Day(-5)), R("a", "1", Day(-5)),
            R("z", "1", Day(-20)), R("a", "2", Day(-1)));

        var ids = (await Store.GetExpiredAsync(Cutoff, 100)).Select(r => $"{r.CheckOutUtc:MMdd}/{r.SupplierId}/{r.ReservationId}").ToList();

        ids.Should().Equal(
            $"{Day(-20):MMdd}/z/1",
            $"{Day(-5):MMdd}/a/1", $"{Day(-5):MMdd}/a/9", $"{Day(-5):MMdd}/b/2",
            $"{Day(-1):MMdd}/a/2");
    }

    [Fact]
    public async Task Keyset_paging_visits_every_expired_row_exactly_once_in_order()
    {
        var rows = Enumerable.Range(0, 25).Select(i => R(i % 3 == 0 ? "a" : "b", $"r{i:D2}", Day(-1 - i % 6))).ToArray();
        await Seed(rows);
        await Seed(R("a", "future", Day(10))); // never returned

        var paged = await AllExpired(pageSize: 7);

        paged.Should().HaveCount(25);
        paged.Select(r => (r.SupplierId, r.ReservationId)).Distinct().Should().HaveCount(25);
        paged.Should().BeInAscendingOrder(r => r.CheckOutUtc);
        paged.Should().BeEquivalentTo(rows);
    }

    [Fact]
    public async Task The_limit_is_honoured()
    {
        await Seed(Enumerable.Range(0, 10).Select(i => R("a", $"r{i}", Day(-1))).ToArray());
        (await Store.GetExpiredAsync(Cutoff, 3)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Deletes_unchanged_rows_and_reports_how_many()
    {
        var rows = new[] { R("a", "1", Day(-3)), R("a", "2", Day(-3)), R("b", "1", Day(-3)) };
        await Seed(rows);
        var page = await Store.GetExpiredAsync(Cutoff, 10);

        (await Store.DeleteIfUnchangedAsync(page)).Should().Be(3);

        foreach (var r in rows) (await Repo.GetAsync(r.SupplierId, r.ReservationId)).Should().BeNull();
        (await Store.GetExpiredAsync(Cutoff, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_a_row_whose_details_changed_after_it_was_read()
    {
        await Seed(R("a", "r1", Day(-3), price: 10000, updatedDay: 1));
        var page = await Store.GetExpiredAsync(Cutoff, 10); // "archived" this version

        // a supplier updates the reservation before we delete (a newer update, even moving it to a future date)
        await Repo.UpsertAsync(R("a", "r1", Day(40), price: 99999, updatedDay: 2));

        (await Store.DeleteIfUnchangedAsync(page)).Should().Be(0);

        var live = await Repo.GetAsync("a", "r1");
        live.Should().NotBeNull("a live, updated reservation must never be deleted by a stale archive pass");
        live!.PriceCents.Should().Be(99999);
    }

    [Fact]
    public async Task Skips_a_row_whose_only_change_is_a_newer_timestamp()
    {
        await Seed(R("a", "r1", Day(-3), updatedDay: 1));
        var page = await Store.GetExpiredAsync(Cutoff, 10);

        await Repo.UpsertAsync(R("a", "r1", Day(-3), updatedDay: 5)); // duplicate details, newer timestamp (freshness advance)

        (await Store.DeleteIfUnchangedAsync(page)).Should().Be(0); // not byte-identical any more: re-examined next run
        (await Repo.GetAsync("a", "r1")).Should().NotBeNull();
    }

    [Fact]
    public async Task In_a_mixed_page_only_the_unchanged_rows_are_deleted()
    {
        await Seed(R("a", "keep", Day(-3)), R("a", "gone1", Day(-3)), R("a", "gone2", Day(-3)));
        var page = await Store.GetExpiredAsync(Cutoff, 10);
        await Repo.UpsertAsync(R("a", "keep", Day(-3), price: 1, updatedDay: 3)); // changed meanwhile

        (await Store.DeleteIfUnchangedAsync(page)).Should().Be(2);

        (await Repo.GetAsync("a", "keep")).Should().NotBeNull();
        (await Repo.GetAsync("a", "gone1")).Should().BeNull();
        (await Repo.GetAsync("a", "gone2")).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_something_that_does_not_exist_or_an_empty_list_deletes_nothing()
    {
        (await Store.DeleteIfUnchangedAsync([R("a", "never", Day(-3))])).Should().Be(0);
        (await Store.DeleteIfUnchangedAsync([])).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_leaves_other_rows_and_the_lifetime_counters_untouched()
    {
        await Seed(R("a", "old", Day(-3)), R("a", "new", Day(30)));
        var before = await _s.Stats.GetAsync("a");
        var page = await Store.GetExpiredAsync(Cutoff, 10);

        await Store.DeleteIfUnchangedAsync(page);

        (await Repo.GetAsync("a", "new")).Should().NotBeNull();
        (await _s.Stats.GetAsync("a")).Should().Be(before); // stats are lifetime totals, not row counts
    }

    [Fact]
    public async Task Reclaiming_space_never_fails_even_when_there_is_nothing_to_reclaim()
    {
        await Store.ReclaimSpaceAsync();
        await Seed(R("a", "old", Day(-3)));
        await Store.DeleteIfUnchangedAsync(await Store.GetExpiredAsync(Cutoff, 10));
        await Store.ReclaimSpaceAsync();
    }
}
