using FluentAssertions;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.RateLimiting;
using Xunit;

namespace Mize.Reservations.UnitTests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ms;
    public void AdvanceMs(long ms) => _ms += ms;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _ms;
}

/// <summary>Scriptable inner limiter that records how often it was actually asked.</summary>
internal sealed class ScriptedLimiter : IRateLimiter
{
    public Func<string, RateLimitDecision> Next { get; set; } = _ => new RateLimitDecision(true, TimeSpan.Zero);
    public Action? OnCall { get; set; }
    public List<string> Calls { get; } = new();

    public Task<RateLimitDecision> TryAcquireAsync(string supplierKey, CancellationToken ct = default)
    {
        Calls.Add(supplierKey);
        OnCall?.Invoke();
        return Task.FromResult(Next(supplierKey));
    }
}

public class BlockCachingRateLimiterTests
{
    private static RateLimitDecision Deny(int ms) => new(false, TimeSpan.FromMilliseconds(ms));
    private static readonly RateLimitDecision Allow = new(true, TimeSpan.Zero);

    private static (BlockCachingRateLimiter limiter, ScriptedLimiter inner, ManualTimeProvider time) Build()
    {
        var inner = new ScriptedLimiter();
        var time = new ManualTimeProvider();
        return (new BlockCachingRateLimiter(inner, time), inner, time);
    }

    [Fact]
    public async Task Allowed_requests_always_reach_the_inner_limiter()
    {
        var (limiter, inner, _) = Build();
        for (var i = 0; i < 5; i++) (await limiter.TryAcquireAsync("acme")).Allowed.Should().BeTrue();
        inner.Calls.Should().HaveCount(5);
    }

    [Fact]
    public async Task After_a_denial_the_supplier_is_rejected_locally_without_calling_the_inner_limiter()
    {
        var (limiter, inner, time) = Build();
        inner.Next = _ => Deny(10_000);

        (await limiter.TryAcquireAsync("acme")).Allowed.Should().BeFalse();
        inner.Calls.Should().HaveCount(1);

        time.AdvanceMs(3_000);
        for (var i = 0; i < 100; i++)
        {
            var d = await limiter.TryAcquireAsync("acme");
            d.Allowed.Should().BeFalse();
            d.RetryAfter.Should().Be(TimeSpan.FromMilliseconds(7_000)); // remaining, not the original
        }
        inner.Calls.Should().HaveCount(1); // the flood never reached Redis
    }

    [Fact]
    public async Task Once_the_block_expires_the_inner_limiter_is_asked_again()
    {
        var (limiter, inner, time) = Build();
        inner.Next = _ => Deny(1_000);
        await limiter.TryAcquireAsync("acme");

        time.AdvanceMs(1_000); // exactly when the slot frees
        inner.Next = _ => Allow;

        (await limiter.TryAcquireAsync("acme")).Allowed.Should().BeTrue();
        inner.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Block_is_measured_from_before_the_inner_call_so_it_never_outlasts_the_real_window()
    {
        // The inner call takes 10 ms of "network + script" time; Redis then says the slot frees in 1000 ms
        // (measured at ITS now, which is later than our t0). Real free time is t0+10+1000 or later; our block
        // ends at t0+1000, so a request at t0+1000 goes to Redis (which decides) instead of being rejected here.
        var (limiter, inner, time) = Build();
        inner.OnCall = () => time.AdvanceMs(10);
        inner.Next = _ => Deny(1_000);
        await limiter.TryAcquireAsync("acme");              // t0 = 0, now clock = 10
        inner.OnCall = null;

        time.AdvanceMs(989);                                 // clock = 999 -> still blocked locally
        (await limiter.TryAcquireAsync("acme")).Allowed.Should().BeFalse();
        inner.Calls.Should().HaveCount(1);

        time.AdvanceMs(1);                                   // clock = 1000 -> block over, ask the inner limiter
        inner.Next = _ => Allow;
        (await limiter.TryAcquireAsync("acme")).Allowed.Should().BeTrue();
        inner.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Suppliers_are_isolated()
    {
        var (limiter, inner, _) = Build();
        inner.Next = key => key == "a" ? Deny(10_000) : Allow;

        (await limiter.TryAcquireAsync("a")).Allowed.Should().BeFalse();
        (await limiter.TryAcquireAsync("b")).Allowed.Should().BeTrue();
        (await limiter.TryAcquireAsync("acme")).Allowed.Should().BeTrue();   // prefix-like ids don't collide
        (await limiter.TryAcquireAsync("a")).Allowed.Should().BeFalse();     // still blocked locally

        inner.Calls.Should().Equal("a", "b", "acme");
    }

    [Fact]
    public async Task Inner_failures_propagate_and_are_never_cached()
    {
        var (limiter, inner, _) = Build();
        inner.Next = _ => throw new RateLimiterUnavailableException("redis down");

        var act = async () => await limiter.TryAcquireAsync("acme");
        await act.Should().ThrowAsync<RateLimiterUnavailableException>();

        inner.Next = _ => Allow; // Redis is back: must be asked again, not blocked by a stale entry
        (await limiter.TryAcquireAsync("acme")).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_new_denial_after_expiry_sets_a_fresh_block()
    {
        var (limiter, inner, time) = Build();
        inner.Next = _ => Deny(5_000);
        await limiter.TryAcquireAsync("acme");

        time.AdvanceMs(5_000);
        inner.Next = _ => Deny(2_000);
        await limiter.TryAcquireAsync("acme");          // new block until t=7000

        time.AdvanceMs(1_999);                          // t=6999
        (await limiter.TryAcquireAsync("acme")).Allowed.Should().BeFalse();
        inner.Calls.Should().HaveCount(2);
    }
}
