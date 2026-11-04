using FluentAssertions;
using Microsoft.Extensions.Options;
using Mize.Reservations.RateLimiting;
using Mize.Reservations.RateLimiting.InMemory;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>Deterministic (manual clock) tests of the in-process limiter's window arithmetic.</summary>
public class InMemoryRateLimiterTests
{
    private static (InMemoryRateLimiter limiter, ManualTimeProvider time) Build(int limit, int windowSeconds)
    {
        var time = new ManualTimeProvider();
        return (new InMemoryRateLimiter(Options.Create(new RateLimitOptions { Limit = limit, WindowSeconds = windowSeconds }), time), time);
    }

    [Fact]
    public async Task Retry_after_is_exactly_when_the_oldest_entry_leaves_the_window()
    {
        var (limiter, time) = Build(limit: 2, windowSeconds: 10);
        await limiter.TryAcquireAsync("a");            // t=0
        time.AdvanceMs(3_000);
        await limiter.TryAcquireAsync("a");            // t=3000
        time.AdvanceMs(2_000);                          // t=5000

        var denied = await limiter.TryAcquireAsync("a");

        denied.Allowed.Should().BeFalse();
        denied.RetryAfter.Should().Be(TimeSpan.FromMilliseconds(5_000)); // oldest (t=0) expires at t=10000
    }

    [Fact]
    public async Task A_slot_frees_exactly_when_the_oldest_entry_expires_and_only_one()
    {
        var (limiter, time) = Build(limit: 2, windowSeconds: 10);
        await limiter.TryAcquireAsync("a");            // t=0
        time.AdvanceMs(3_000);
        await limiter.TryAcquireAsync("a");            // t=3000

        time.AdvanceMs(6_999);                          // t=9999: nothing expired yet
        (await limiter.TryAcquireAsync("a")).Allowed.Should().BeFalse();

        time.AdvanceMs(1);                              // t=10000: the t=0 entry leaves the window
        (await limiter.TryAcquireAsync("a")).Allowed.Should().BeTrue();
        (await limiter.TryAcquireAsync("a")).Allowed.Should().BeFalse(); // the t=3000 entry is still inside
    }

    [Fact]
    public async Task Denied_requests_are_not_recorded()
    {
        var (limiter, time) = Build(limit: 1, windowSeconds: 10);
        await limiter.TryAcquireAsync("a");            // t=0
        for (var i = 0; i < 50; i++) (await limiter.TryAcquireAsync("a")).Allowed.Should().BeFalse();

        time.AdvanceMs(10_000);                         // had denials been recorded the window would still be full

        (await limiter.TryAcquireAsync("a")).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Suppliers_do_not_share_windows()
    {
        var (limiter, _) = Build(limit: 1, windowSeconds: 10);
        (await limiter.TryAcquireAsync("a")).Allowed.Should().BeTrue();
        (await limiter.TryAcquireAsync("a")).Allowed.Should().BeFalse();
        (await limiter.TryAcquireAsync("b")).Allowed.Should().BeTrue();
        (await limiter.TryAcquireAsync("a2")).Allowed.Should().BeTrue();
    }
}
