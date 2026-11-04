using FluentAssertions;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Xunit;

namespace Mize.Reservations.UnitTests;

internal sealed class FixedUtcTimeProvider(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
}

public class RetentionPolicyTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Reservation StayEndingOn(DateTime checkOut) =>
        new("acme", "r1", "room", checkOut.AddDays(-2), checkOut, 100, Now);

    [Fact]
    public void Disabled_policy_never_expires_anything() =>
        RetentionPolicy.Disabled.IsExpired(StayEndingOn(new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Now).Should().BeFalse();

    [Fact]
    public void Cutoff_is_now_minus_the_retention_days() =>
        new RetentionPolicy(Enabled: true, RetentionDays: 365).CutoffUtc(Now).Should().Be(Now.AddDays(-365));

    [Fact]
    public void A_stay_ending_exactly_on_the_cutoff_is_not_expired_but_one_tick_earlier_is()
    {
        var policy = new RetentionPolicy(Enabled: true, RetentionDays: 30);
        var cutoff = policy.CutoffUtc(Now);

        policy.IsExpired(StayEndingOn(cutoff), Now).Should().BeFalse();
        policy.IsExpired(StayEndingOn(cutoff.AddTicks(-1)), Now).Should().BeTrue();
    }

    [Fact]
    public void From_reads_the_Retention_keys_and_falls_back_to_safe_defaults()
    {
        var values = new Dictionary<string, string?>
        {
            ["Retention:Enabled"] = "true", ["Retention:RetentionDays"] = "400", ["Retention:BatchSize"] = "50",
            ["Retention:MaxBatchesPerRun"] = "3", ["Retention:DryRun"] = "true"
        };
        RetentionPolicy.From(k => values.GetValueOrDefault(k)).Should().Be(new RetentionPolicy(true, 400, 50, 3, true));

        RetentionPolicy.From(_ => null).Should().Be(new RetentionPolicy()); // nothing configured: disabled
        RetentionPolicy.From(k => k == "Retention:RetentionDays" ? "-5" : null).RetentionDays.Should().Be(365); // junk ignored
        RetentionPolicy.From(k => k == "Retention:Enabled" ? "maybe" : null).Enabled.Should().BeFalse();
    }
}

/// <summary>The resurrection guard on ingest.</summary>
public class IngestExpiredTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static IngestCommand Cmd(DateTime checkOut, string id = "r1") => new("acme", id, "room1",
        new DateTimeOffset(checkOut.AddDays(-2), TimeSpan.Zero), new DateTimeOffset(checkOut, TimeSpan.Zero), 450m,
        new DateTimeOffset(Now.AddDays(-1), TimeSpan.Zero));

    private static (IngestService svc, FakeReservations repo, FakeStats stats) Build(RetentionPolicy? policy)
    {
        var repo = new FakeReservations(); var stats = new FakeStats();
        return (new IngestService(new FakeRateLimiter(100), repo, stats, policy, new FixedUtcTimeProvider(Now)), repo, stats);
    }

    [Fact]
    public async Task With_retention_enabled_an_update_for_a_stay_past_the_cutoff_is_Expired_and_never_stored()
    {
        var (svc, repo, stats) = Build(new RetentionPolicy(Enabled: true, RetentionDays: 365));

        var result = await svc.IngestAsync(Cmd(Now.AddDays(-400)));

        result.Outcome.Should().Be(IngestOutcome.Expired);
        result.Changed.Should().BeFalse();
        repo.Received.Should().BeEmpty();       // never reaches storage: no resurrection of a purged reservation
        stats.Throttled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stay_inside_the_retention_window_is_accepted()
    {
        var (svc, repo, _) = Build(new RetentionPolicy(Enabled: true, RetentionDays: 365));

        (await svc.IngestAsync(Cmd(Now.AddDays(-300)))).Outcome.Should().Be(IngestOutcome.Created);
        repo.Received.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_boundary_is_strict_a_stay_ending_exactly_on_the_cutoff_is_accepted()
    {
        var policy = new RetentionPolicy(Enabled: true, RetentionDays: 365);
        var (svc, _, _) = Build(policy);

        (await svc.IngestAsync(Cmd(policy.CutoffUtc(Now)))).Outcome.Should().Be(IngestOutcome.Created);
        (await svc.IngestAsync(Cmd(policy.CutoffUtc(Now).AddSeconds(-1), id: "r2"))).Outcome.Should().Be(IngestOutcome.Expired);
    }

    [Theory]
    [InlineData(false)]  // explicit disabled policy
    [InlineData(true)]   // no policy at all
    public async Task Without_retention_old_stays_are_accepted_as_before(bool nullPolicy)
    {
        var (svc, repo, _) = Build(nullPolicy ? null : RetentionPolicy.Disabled);

        (await svc.IngestAsync(Cmd(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)))).Outcome.Should().Be(IngestOutcome.Created);
        repo.Received.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_Expired_request_still_costs_rate_limit_quota()
    {
        var limiter = new FakeRateLimiter(100);
        var svc = new IngestService(limiter, new FakeReservations(), new FakeStats(),
            new RetentionPolicy(Enabled: true, RetentionDays: 365), new FixedUtcTimeProvider(Now));

        await svc.IngestAsync(Cmd(Now.AddDays(-400)));

        limiter.Calls["acme"].Should().Be(1);
    }

    [Fact]
    public async Task Invalid_payloads_are_still_reported_as_Invalid_not_Expired()
    {
        var (svc, _, _) = Build(new RetentionPolicy(Enabled: true, RetentionDays: 365));

        var result = await svc.IngestAsync(Cmd(Now.AddDays(-400)) with { Price = -1m });

        result.Outcome.Should().Be(IngestOutcome.Invalid);
    }
}
