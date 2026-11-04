using FluentAssertions;
using Mize.Reservations.Api.Observability;
using Mize.Reservations.Core.Models;
using Mize.Reservations.RateLimiting;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>
/// Supplier ids are not a bounded set, so anything keyed by supplier must clean up after itself or it leaks. Both tables prune only
/// once they pass 10,000 keys, which is why these need a fake clock and thousands of keys.
/// </summary>
public class BoundedMemoryTests
{
    [Fact]
    public void The_log_sampler_forgets_keys_that_have_been_quiet_for_two_intervals_once_the_table_is_large()
    {
        var time = new ManualTimeProvider();
        var sampler = new LogSampler(time);
        var interval = TimeSpan.FromSeconds(1);

        for (var i = 0; i <= 10_000; i++) sampler.ShouldLog($"supplier-{i}", interval, out _);   // 10,001 keys, each logged once
        sampler.TrackedKeys.Should().Be(10_001, "nothing is old enough to forget yet");

        time.AdvanceMs(3_000);                                                                    // more than two intervals later
        sampler.ShouldLog("new-supplier", interval, out _).Should().BeTrue();

        sampler.TrackedKeys.Should().Be(1, "the 10,001 quiet keys were dropped; only the fresh one remains");
    }

    [Fact]
    public void The_log_sampler_keeps_a_key_that_still_has_suppressed_events_to_report()
    {
        var time = new ManualTimeProvider();
        var sampler = new LogSampler(time);
        var interval = TimeSpan.FromSeconds(1);

        sampler.ShouldLog("noisy", interval, out _).Should().BeTrue();
        sampler.ShouldLog("noisy", interval, out _).Should().BeFalse();      // one event skipped: its count must not be lost
        for (var i = 0; i <= 10_000; i++) sampler.ShouldLog($"quiet-{i}", interval, out _);

        time.AdvanceMs(3_000);
        sampler.ShouldLog("trigger", interval, out _);

        sampler.TrackedKeys.Should().Be(2, "'noisy' (has a suppressed count) and 'trigger'");
        time.AdvanceMs(3_000);
        sampler.ShouldLog("noisy", interval, out var skipped).Should().BeTrue();
        skipped.Should().Be(1, "the skipped event is still reported on the next line");
    }

    [Fact]
    public async Task The_block_cache_forgets_expired_blocks_once_the_table_is_large()
    {
        var inner = new ScriptedLimiter { Next = _ => new RateLimitDecision(false, TimeSpan.FromSeconds(1)) };
        var time = new ManualTimeProvider();
        var limiter = new BlockCachingRateLimiter(inner, time);

        for (var i = 0; i <= 10_000; i++) await limiter.TryAcquireAsync($"supplier-{i}");        // 10,001 blocked suppliers
        limiter.BlockedSuppliers.Should().Be(10_001, "every block is still in force");

        time.AdvanceMs(2_000);                                                                    // all of them have expired
        await limiter.TryAcquireAsync("new-supplier");

        limiter.BlockedSuppliers.Should().Be(1, "the expired blocks were dropped; only the new one remains");
    }

    [Fact]
    public async Task The_block_cache_never_drops_a_block_that_is_still_in_force()
    {
        var inner = new ScriptedLimiter();
        inner.Next = key => key == "long" ? new RateLimitDecision(false, TimeSpan.FromSeconds(60)) : new RateLimitDecision(false, TimeSpan.FromSeconds(1));
        var time = new ManualTimeProvider();
        var limiter = new BlockCachingRateLimiter(inner, time);

        await limiter.TryAcquireAsync("long");
        for (var i = 0; i <= 10_000; i++) await limiter.TryAcquireAsync($"short-{i}");
        time.AdvanceMs(2_000);
        await limiter.TryAcquireAsync("trigger");

        limiter.BlockedSuppliers.Should().Be(2, "'long' (still blocked for ~58 s) and 'trigger'");
        var calls = inner.Calls.Count;
        (await limiter.TryAcquireAsync("long")).Allowed.Should().BeFalse();
        inner.Calls.Should().HaveCount(calls, "the surviving block still answers locally without asking the inner limiter");
    }
}
