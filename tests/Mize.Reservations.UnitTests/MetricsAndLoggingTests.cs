using FluentAssertions;
using Mize.Reservations.Api.Observability;
using Mize.Reservations.Contracts;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Mize.Reservations.Core.Telemetry;
using Mize.Reservations.RateLimiting;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>Every ingest outcome is counted once and timed once, under its own outcome tag.</summary>
public sealed class IngestMetricsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly MeterProbe _probe = new();

    public void Dispose() => _probe.Dispose();

    private IngestService Service(IRateLimiter? limiter = null, IReservationRepository? repo = null, RetentionPolicy? retention = null) =>
        new(limiter ?? new FakeRateLimiter(100), repo ?? new FakeReservations(), new FakeStats(), retention, new FixedUtcTimeProvider(Now), _probe.NewMetrics());

    private static IngestCommand Cmd(string supplier = "acme", DateTime? checkOut = null)
    {
        var co = checkOut ?? Now.AddDays(10);
        return new(supplier, "r1", "room1", new DateTimeOffset(co.AddDays(-2), TimeSpan.Zero), new DateTimeOffset(co, TimeSpan.Zero),
            450m, new DateTimeOffset(Now.AddDays(-1), TimeSpan.Zero));
    }

    private void AssertCountedOnce(string outcome)
    {
        _probe.Sum(ReservationMetrics.IngestRequestsName, ("outcome", outcome)).Should().Be(1);
        _probe.Sum(ReservationMetrics.IngestRequestsName).Should().Be(1, "no other outcome may be counted for this request");
        _probe.Count(ReservationMetrics.IngestDurationName, ("outcome", outcome)).Should().Be(1);
        _probe.Values(ReservationMetrics.IngestDurationName).Single().Should().BeGreaterThanOrEqualTo(0);
    }

    [Theory]
    [InlineData(IngestOutcome.Created, "created")]
    [InlineData(IngestOutcome.Updated, "updated")]
    [InlineData(IngestOutcome.Unchanged, "unchanged")]
    [InlineData(IngestOutcome.Stale, "stale")]
    public async Task Storage_outcomes_are_counted_under_their_name(IngestOutcome outcome, string tag)
    {
        await Service(repo: new FakeReservations { Next = outcome }).IngestAsync(Cmd());
        AssertCountedOnce(tag);
    }

    [Fact]
    public async Task A_throttled_request_is_counted_as_throttled()
    {
        await Service(limiter: new FakeRateLimiter(limit: 0)).IngestAsync(Cmd());
        AssertCountedOnce("throttled");
    }

    [Fact]
    public async Task An_invalid_request_is_counted_as_invalid_even_before_the_limiter_is_reached()
    {
        await Service().IngestAsync(Cmd(supplier: "  "));
        AssertCountedOnce("invalid");
    }

    [Fact]
    public async Task An_expired_request_is_counted_as_expired()
    {
        await Service(retention: new RetentionPolicy(Enabled: true, RetentionDays: 365)).IngestAsync(Cmd(checkOut: Now.AddDays(-400)));
        AssertCountedOnce("expired");
    }

    [Fact]
    public async Task A_failure_is_counted_as_error_and_still_propagates()
    {
        var act = async () => await Service(limiter: new ThrowingLimiter()).IngestAsync(Cmd());

        await act.Should().ThrowAsync<RateLimiterUnavailableException>();
        AssertCountedOnce("error");
    }

    [Fact]
    public async Task Metrics_are_never_tagged_with_a_supplier_because_supplier_ids_are_unbounded()
    {
        await Service().IngestAsync(Cmd(supplier: "acme"));
        await Service().IngestAsync(Cmd(supplier: "other"));

        var tagged = _probe.Sum(ReservationMetrics.IngestRequestsName, ("supplier", "acme")) + _probe.Sum(ReservationMetrics.IngestRequestsName, ("supplierId", "acme"));
        tagged.Should().Be(0);
        _probe.Sum(ReservationMetrics.IngestRequestsName, ("outcome", "created")).Should().Be(2); // aggregated across suppliers
    }

    [Fact]
    public async Task Without_metrics_the_service_works_exactly_the_same()
    {
        var svc = new IngestService(new FakeRateLimiter(100), new FakeReservations(), new FakeStats());
        (await svc.IngestAsync(Cmd())).Outcome.Should().Be(IngestOutcome.Created);
    }

    private sealed class ThrowingLimiter : IRateLimiter
    {
        public Task<RateLimitDecision> TryAcquireAsync(string supplierKey, CancellationToken ct = default) =>
            throw new RateLimiterUnavailableException("redis down");
    }
}

/// <summary>The decorator is the one place that sees every rate-limit decision.</summary>
public sealed class RateLimitMetricsTests : IDisposable
{
    private readonly MeterProbe _probe = new();
    private readonly ManualTimeProvider _time = new();

    public void Dispose() => _probe.Dispose();

    private double Decisions(string result) => _probe.Sum(ReservationMetrics.RateLimitDecisionsName, ("result", result));

    private BlockCachingRateLimiter Build(ScriptedLimiter inner) => new(inner, _time, _probe.NewMetrics());

    [Fact]
    public async Task Allowed_and_denied_decisions_come_from_the_inner_limiter()
    {
        var inner = new ScriptedLimiter();
        var limiter = Build(inner);

        await limiter.TryAcquireAsync("a");
        inner.Next = _ => new RateLimitDecision(false, TimeSpan.FromMilliseconds(1));
        _time.AdvanceMs(10);
        await limiter.TryAcquireAsync("b");

        Decisions("allowed").Should().Be(1);
        Decisions("denied").Should().Be(1);
    }

    [Fact]
    public async Task Requests_rejected_by_the_local_block_cache_are_counted_separately_and_never_reach_the_inner_limiter()
    {
        var inner = new ScriptedLimiter { Next = _ => new RateLimitDecision(false, TimeSpan.FromSeconds(30)) };
        var limiter = Build(inner);

        await limiter.TryAcquireAsync("noisy");                       // denied by the inner limiter
        for (var i = 0; i < 5; i++) await limiter.TryAcquireAsync("noisy"); // blocked locally

        Decisions("denied").Should().Be(1);
        Decisions("blocked_locally").Should().Be(5);
        inner.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_store_outage_is_counted_as_unavailable_and_still_propagates()
    {
        var inner = new ScriptedLimiter { Next = _ => throw new RateLimiterUnavailableException("down") };
        var limiter = Build(inner);

        var act = async () => await limiter.TryAcquireAsync("a");

        await act.Should().ThrowAsync<RateLimiterUnavailableException>();
        Decisions("unavailable").Should().Be(1);
        Decisions("allowed").Should().Be(0);
    }
}

/// <summary>Floods and outages must not bury the logs; the counts live in metrics.</summary>
public class LogSamplerTests
{
    private static (LogSampler sampler, ManualTimeProvider time) Build()
    {
        var time = new ManualTimeProvider();
        return (new LogSampler(time), time);
    }

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    [Fact]
    public void The_first_event_is_logged_with_nothing_suppressed()
    {
        var (sampler, _) = Build();
        sampler.ShouldLog("k", Interval, out var suppressed).Should().BeTrue();
        suppressed.Should().Be(0);
    }

    [Fact]
    public void Repeats_inside_the_interval_are_suppressed_and_counted_then_reported_on_the_next_logged_line()
    {
        var (sampler, time) = Build();
        sampler.ShouldLog("k", Interval, out _);

        for (var i = 0; i < 7; i++) sampler.ShouldLog("k", Interval, out _).Should().BeFalse();
        time.AdvanceMs(10_000);

        sampler.ShouldLog("k", Interval, out var suppressed).Should().BeTrue();
        suppressed.Should().Be(7);

        sampler.ShouldLog("k", Interval, out _).Should().BeFalse();       // a fresh interval starts
        time.AdvanceMs(10_000);
        sampler.ShouldLog("k", Interval, out var again).Should().BeTrue();
        again.Should().Be(1);
    }

    [Fact]
    public void Keys_are_independent()
    {
        var (sampler, _) = Build();
        sampler.ShouldLog("a", Interval, out _).Should().BeTrue();
        sampler.ShouldLog("b", Interval, out _).Should().BeTrue();
        sampler.ShouldLog("a", Interval, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Under_concurrency_exactly_one_caller_wins_per_interval_and_nothing_is_miscounted()
    {
        var (sampler, time) = Build();

        var results = await Task.WhenAll(Enumerable.Range(0, 1000).Select(i => Task.Run(() => sampler.ShouldLog("k", Interval, out _))));
        results.Count(r => r).Should().Be(1);

        time.AdvanceMs(10_000);
        sampler.ShouldLog("k", Interval, out var suppressed).Should().BeTrue();
        suppressed.Should().Be(999);
    }
}
