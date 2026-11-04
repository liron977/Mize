using FluentAssertions;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>Counts per supplier key with a fixed limit; never expires (service-level logic only).</summary>
internal sealed class FakeRateLimiter(int limit) : IRateLimiter
{
    public Dictionary<string, int> Calls { get; } = new();
    public Task<RateLimitDecision> TryAcquireAsync(string key, CancellationToken ct = default)
    {
        Calls.TryGetValue(key, out var n);
        if (n >= limit) return Task.FromResult(new RateLimitDecision(false, TimeSpan.FromSeconds(30)));
        Calls[key] = n + 1;
        return Task.FromResult(new RateLimitDecision(true, TimeSpan.Zero));
    }
}

internal sealed class FakeReservations : IReservationRepository
{
    public List<Reservation> Received { get; } = new();
    public IngestOutcome Next { get; set; } = IngestOutcome.Created;
    public Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken ct = default) =>
        Task.FromResult<Reservation?>(Received.LastOrDefault(r => r.SupplierId == supplierId && r.ReservationId == reservationId));
    public Task<IngestOutcome> UpsertAsync(Reservation r, CancellationToken ct = default)
    {
        Received.Add(r);
        return Task.FromResult(Next);
    }
}

internal sealed class FakeStats : IStatsRepository
{
    public Dictionary<string, int> Throttled { get; } = new();
    public Task IncrementThrottledAsync(string supplierId, CancellationToken ct = default)
    {
        Throttled[supplierId] = Throttled.GetValueOrDefault(supplierId) + 1;
        return Task.CompletedTask;
    }
    public Task<SupplierStats> GetAsync(string supplierId, CancellationToken ct = default) =>
        Task.FromResult(new SupplierStats(supplierId, 0, Throttled.GetValueOrDefault(supplierId), 0, 0));
}

public class IngestServiceTests
{
    private static IngestCommand Cmd(string supplier, string id = "r1") => new(supplier, id, "room1",
        DateTimeOffset.Parse("2026-08-01T00:00:00Z"), DateTimeOffset.Parse("2026-08-03T00:00:00Z"),
        450m, DateTimeOffset.Parse("2026-07-01T10:15:00Z"));

    private static (IngestService svc, FakeRateLimiter limiter, FakeReservations repo, FakeStats stats) Build(int limit = 100)
    {
        var limiter = new FakeRateLimiter(limit);
        var repo = new FakeReservations();
        var stats = new FakeStats();
        return (new IngestService(limiter, repo, stats), limiter, repo, stats);
    }

    [Fact]
    public async Task Missing_supplier_is_invalid_and_does_not_touch_limiter_or_repo()
    {
        var (svc, limiter, repo, _) = Build();
        var r = await svc.IngestAsync(Cmd("  "));
        r.Outcome.Should().Be(IngestOutcome.Invalid);
        limiter.Calls.Should().BeEmpty();
        repo.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_over_limit_is_throttled_counted_and_never_reaches_repo()
    {
        var (svc, _, repo, stats) = Build(limit: 3);
        for (var i = 0; i < 3; i++) (await svc.IngestAsync(Cmd("acme", $"r{i}"))).Outcome.Should().Be(IngestOutcome.Created);

        var r = await svc.IngestAsync(Cmd("acme", "r99"));

        r.Outcome.Should().Be(IngestOutcome.Throttled);
        r.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
        repo.Received.Should().HaveCount(3);
        stats.Throttled["acme"].Should().Be(1);
    }

    [Fact]
    public async Task Throttling_one_supplier_does_not_affect_another()
    {
        var (svc, _, _, stats) = Build(limit: 2);
        await svc.IngestAsync(Cmd("a")); await svc.IngestAsync(Cmd("a"));
        (await svc.IngestAsync(Cmd("a"))).Outcome.Should().Be(IngestOutcome.Throttled);

        (await svc.IngestAsync(Cmd("b"))).Outcome.Should().Be(IngestOutcome.Created);
        stats.Throttled.Should().ContainKey("a").And.NotContainKey("b");
    }

    [Fact]
    public async Task Supplier_case_and_whitespace_variants_share_one_quota()
    {
        var (svc, limiter, _, _) = Build(limit: 2);
        await svc.IngestAsync(Cmd("Acme"));
        await svc.IngestAsync(Cmd(" ACME "));
        (await svc.IngestAsync(Cmd("acme"))).Outcome.Should().Be(IngestOutcome.Throttled);
        limiter.Calls.Keys.Should().BeEquivalentTo("acme");
    }

    [Fact]
    public async Task Invalid_payload_still_costs_quota_and_is_not_persisted()
    {
        var (svc, limiter, repo, _) = Build();
        var r = await svc.IngestAsync(Cmd("acme") with { RoomId = null });
        r.Outcome.Should().Be(IngestOutcome.Invalid);
        limiter.Calls["acme"].Should().Be(1);
        repo.Received.Should().BeEmpty();
    }

    [Theory]
    [InlineData(IngestOutcome.Created, true)]
    [InlineData(IngestOutcome.Updated, true)]
    [InlineData(IngestOutcome.Unchanged, false)]
    [InlineData(IngestOutcome.Stale, false)]
    public async Task Repository_outcome_is_passed_through(IngestOutcome outcome, bool changed)
    {
        var (svc, _, repo, _) = Build();
        repo.Next = outcome;
        var r = await svc.IngestAsync(Cmd("acme"));
        r.Outcome.Should().Be(outcome);
        r.Changed.Should().Be(changed);
        repo.Received.Single().SupplierId.Should().Be("acme");
    }
}

public class ReservationEqualityTests
{
    private static Reservation Base() => new("a", "r", "room", new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc), 45000, new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact] public void Timestamp_only_difference_is_same_details() =>
        Base().HasSameDetailsAs(Base() with { UpdatedAtUtc = new(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc) }).Should().BeTrue();

    [Fact] public void Price_difference_is_different() =>
        Base().HasSameDetailsAs(Base() with { PriceCents = 44000 }).Should().BeFalse();

    [Fact] public void Room_difference_is_different() =>
        Base().HasSameDetailsAs(Base() with { RoomId = "other" }).Should().BeFalse();

    [Fact] public void Date_difference_is_different() =>
        Base().HasSameDetailsAs(Base() with { CheckOutUtc = new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc) }).Should().BeFalse();
}
