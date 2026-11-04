using FluentAssertions;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Xunit;

namespace Mize.Reservations.Contracts;

/// <summary>The storage adapters under test, plus how to clean up after them.</summary>
public sealed class StorageUnderTest(
    IReservationRepository reservations, IStatsRepository stats, Func<Task>? cleanup = null,
    IReservationRetentionStore? retention = null) : IAsyncDisposable
{
    public IReservationRepository Reservations { get; } = reservations;
    public IStatsRepository Stats { get; } = stats;

    /// <summary>The retention side of the same engine; required by <see cref="RetentionStoreContract"/>.</summary>
    public IReservationRetentionStore? Retention { get; } = retention;

    public async ValueTask DisposeAsync()
    {
        if (cleanup is not null) await cleanup();
    }
}

/// <summary>
/// Behavioural contract every storage engine must satisfy for <see cref="IReservationRepository"/> and
/// <see cref="IStatsRepository"/>. To validate a new engine, derive from this class in that engine's test project and
/// implement <see cref="CreateAsync"/> (a fresh, empty, initialised store per test); xUnit runs every test below
/// against it. Only the ports are used to observe state, so nothing here is engine-specific.
/// </summary>
public abstract class StorageContract : IAsyncLifetime
{
    private StorageUnderTest _s = null!;

    protected abstract Task<StorageUnderTest> CreateAsync();

    protected IReservationRepository Repo => _s.Reservations;
    protected IStatsRepository Stats => _s.Stats;

    public async Task InitializeAsync() => _s = await CreateAsync();
    public async Task DisposeAsync() => await _s.DisposeAsync();

    private static DateTime T(int day) => new(2026, 7, day, 10, 0, 0, DateTimeKind.Utc);

    protected static Reservation R(string supplier = "acme", string id = "r1", string room = "room1",
        long price = 45000, int updatedDay = 1) =>
        new(supplier, id, room, new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc), price, T(updatedDay));

    [Fact]
    public async Task New_reservation_is_created_and_readable_immediately_after_the_call_returns()
    {
        (await Repo.UpsertAsync(R())).Should().Be(IngestOutcome.Created);

        var stored = await Repo.GetAsync("acme", "r1"); // acknowledged => durable and visible
        stored.Should().NotBeNull();
        stored!.PriceCents.Should().Be(45000);
        stored.RoomId.Should().Be("room1");
        stored.CheckInUtc.Should().Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        stored.UpdatedAtUtc.Should().Be(T(1));
    }

    [Fact]
    public async Task GetAsync_returns_null_for_an_unknown_reservation()
    {
        (await Repo.GetAsync("acme", "nope")).Should().BeNull();
        await Repo.UpsertAsync(R());
        (await Repo.GetAsync("acme", "other")).Should().BeNull();
        (await Repo.GetAsync("other", "r1")).Should().BeNull();
    }

    [Fact]
    public async Task Identical_resend_is_unchanged_and_not_rewritten()
    {
        await Repo.UpsertAsync(R());
        (await Repo.UpsertAsync(R())).Should().Be(IngestOutcome.Unchanged);

        var s = await Stats.GetAsync("acme");
        (s.Ingested, s.Unchanged).Should().Be((1, 1));
    }

    [Fact]
    public async Task Only_timestamp_differs_is_unchanged_but_freshness_marker_advances()
    {
        await Repo.UpsertAsync(R(updatedDay: 1));
        (await Repo.UpsertAsync(R(updatedDay: 5))).Should().Be(IngestOutcome.Unchanged);

        var stored = await Repo.GetAsync("acme", "r1");
        stored!.UpdatedAtUtc.Should().Be(T(5));
        stored.PriceCents.Should().Be(45000); // details untouched
    }

    [Fact]
    public async Task Identical_details_with_older_timestamp_is_unchanged_and_marker_never_moves_back()
    {
        await Repo.UpsertAsync(R(updatedDay: 5));
        (await Repo.UpsertAsync(R(updatedDay: 1))).Should().Be(IngestOutcome.Unchanged);
        (await Repo.GetAsync("acme", "r1"))!.UpdatedAtUtc.Should().Be(T(5));
    }

    [Fact]
    public async Task Delayed_older_update_cannot_overwrite_state_reconfirmed_by_a_newer_duplicate()
    {
        // T1: D stored. T5: duplicate of D. T3: delayed update D' arrives afterwards.
        await Repo.UpsertAsync(R(price: 45000, updatedDay: 1));
        (await Repo.UpsertAsync(R(price: 45000, updatedDay: 5))).Should().Be(IngestOutcome.Unchanged);

        (await Repo.UpsertAsync(R(price: 40000, updatedDay: 3))).Should().Be(IngestOutcome.Stale);

        (await Repo.GetAsync("acme", "r1"))!.PriceCents.Should().Be(45000); // final state is D, not D'
    }

    [Theory]
    [InlineData("price")]
    [InlineData("room")]
    public async Task Different_details_with_newer_timestamp_updates_the_record(string field)
    {
        await Repo.UpsertAsync(R(updatedDay: 1));
        var changed = field == "price" ? R(price: 40000, updatedDay: 2) : R(room: "room2", updatedDay: 2);

        (await Repo.UpsertAsync(changed)).Should().Be(IngestOutcome.Updated);

        var stored = await Repo.GetAsync("acme", "r1");
        stored!.PriceCents.Should().Be(field == "price" ? 40000 : 45000);
        stored.RoomId.Should().Be(field == "room" ? "room2" : "room1");
        stored.UpdatedAtUtc.Should().Be(T(2));
        (await Stats.GetAsync("acme")).Ingested.Should().Be(2);
    }

    [Fact]
    public async Task Different_details_with_equal_timestamp_is_applied()
    {
        await Repo.UpsertAsync(R(updatedDay: 3));
        (await Repo.UpsertAsync(R(price: 100, updatedDay: 3))).Should().Be(IngestOutcome.Updated);
        (await Repo.GetAsync("acme", "r1"))!.PriceCents.Should().Be(100);
    }

    [Fact]
    public async Task Different_details_with_older_timestamp_is_stale_and_ignored()
    {
        await Repo.UpsertAsync(R(price: 45000, updatedDay: 5));
        (await Repo.UpsertAsync(R(price: 100, updatedDay: 1))).Should().Be(IngestOutcome.Stale);

        (await Repo.GetAsync("acme", "r1"))!.PriceCents.Should().Be(45000);
        (await Stats.GetAsync("acme")).Stale.Should().Be(1);
    }

    [Fact]
    public async Task Same_reservation_id_under_different_suppliers_are_independent()
    {
        (await Repo.UpsertAsync(R(supplier: "a", price: 1))).Should().Be(IngestOutcome.Created);
        (await Repo.UpsertAsync(R(supplier: "b", price: 2))).Should().Be(IngestOutcome.Created);

        (await Repo.GetAsync("a", "r1"))!.PriceCents.Should().Be(1);
        (await Repo.GetAsync("b", "r1"))!.PriceCents.Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_identical_requests_produce_exactly_one_create()
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => Repo.UpsertAsync(R()))));

        outcomes.Count(o => o == IngestOutcome.Created).Should().Be(1);
        outcomes.Count(o => o == IngestOutcome.Unchanged).Should().Be(49);
        var s = await Stats.GetAsync("acme");
        (s.Ingested, s.Unchanged).Should().Be((1, 49)); // no lost counter updates
    }

    [Fact]
    public async Task Counters_and_outcomes_are_exact_under_heavy_concurrency()
    {
        // 5 suppliers x 40 distinct reservations, each sent 3 times (1 create + 2 identical duplicates),
        // plus 25 throttle events per supplier, all concurrently.
        var tasks = new List<Task>();
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<IngestOutcome>();
        for (var s = 0; s < 5; s++)
            for (var i = 0; i < 40; i++)
                for (var rep = 0; rep < 3; rep++)
                {
                    var (sup, id) = ($"sup{s}", $"r{i}");
                    tasks.Add(Task.Run(async () => outcomes.Add(await Repo.UpsertAsync(R(sup, id)))));
                }
        for (var s = 0; s < 5; s++)
            for (var t = 0; t < 25; t++)
            {
                var sup = $"sup{s}";
                tasks.Add(Task.Run(() => Stats.IncrementThrottledAsync(sup)));
            }
        await Task.WhenAll(tasks);

        outcomes.Count(o => o == IngestOutcome.Created).Should().Be(200);
        outcomes.Count(o => o == IngestOutcome.Unchanged).Should().Be(400);
        for (var s = 0; s < 5; s++)
        {
            var st = await Stats.GetAsync($"sup{s}");
            (st.Ingested, st.Unchanged, st.Throttled).Should().Be((40, 80, 25), $"supplier sup{s}");
        }
    }

    [Fact]
    public async Task Throttled_counter_is_per_supplier_exact_and_unknown_suppliers_read_as_zero()
    {
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => Stats.IncrementThrottledAsync("a"))));
        await Stats.IncrementThrottledAsync("b");

        (await Stats.GetAsync("a")).Throttled.Should().Be(40);
        (await Stats.GetAsync("b")).Throttled.Should().Be(1);
        (await Stats.GetAsync("never-seen")).Should().Be(new SupplierStats("never-seen", 0, 0, 0, 0));
    }

    [Fact]
    public async Task A_throttle_increment_is_durable_once_acknowledged()
    {
        for (var i = 1; i <= 25; i++)
        {
            await Stats.IncrementThrottledAsync("acme");
            (await Stats.GetAsync("acme")).Throttled.Should().Be(i); // sequential awaits: visible every time
        }
    }

    [Fact]
    public async Task Concurrent_throttles_across_many_suppliers_count_exactly()
    {
        await Task.WhenAll(Enumerable.Range(0, 8 * 300).Select(i => Task.Run(() => Stats.IncrementThrottledAsync($"s{i % 8}"))));

        for (var s = 0; s < 8; s++) (await Stats.GetAsync($"s{s}")).Throttled.Should().Be(300);
    }
}
