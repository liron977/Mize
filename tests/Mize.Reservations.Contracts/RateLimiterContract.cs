using FluentAssertions;
using Mize.Reservations.Core.Abstractions;
using Xunit;

namespace Mize.Reservations.Contracts;

/// <summary>
/// Behavioural contract every <see cref="IRateLimiter"/> must satisfy. To validate a new implementation, derive from
/// this class in the implementation's test project and implement <see cref="CreateLimiter"/>; xUnit then runs every
/// test below against it. Limiters that are shared across app instances must derive from
/// <see cref="DistributedRateLimiterContract"/> instead.
/// </summary>
public abstract class RateLimiterContract : IAsyncLifetime
{
    /// <summary>A fresh limiter with its own isolated state, enforcing <paramref name="limit"/> per rolling window.</summary>
    protected abstract IRateLimiter CreateLimiter(int limit, int windowSeconds);

    public virtual Task InitializeAsync() => Task.CompletedTask;
    public virtual Task DisposeAsync() => Task.CompletedTask;

    protected static string Supplier() => "s-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Allows_exactly_the_limit_then_denies_with_retry_after()
    {
        var limiter = CreateLimiter(limit: 100, windowSeconds: 60);
        var s = Supplier();

        for (var i = 0; i < 100; i++) (await limiter.TryAcquireAsync(s)).Allowed.Should().BeTrue($"request {i + 1}");

        var denied = await limiter.TryAcquireAsync(s);
        denied.Allowed.Should().BeFalse();
        denied.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Window_clears_after_it_elapses()
    {
        var limiter = CreateLimiter(limit: 3, windowSeconds: 2);
        var s = Supplier();
        for (var i = 0; i < 3; i++) await limiter.TryAcquireAsync(s);
        (await limiter.TryAcquireAsync(s)).Allowed.Should().BeFalse();

        await Task.Delay(TimeSpan.FromMilliseconds(2300));

        (await limiter.TryAcquireAsync(s)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Window_is_rolling_not_fixed_bucket()
    {
        // limit 4 / 3s window: 2 requests at t=0 (pair A), 2 at t~1.6s (pair B), then the 5th is denied until pair A expires
        // (t~3s), at which point exactly 2 slots free up, not 4.
        //
        // This is the only contract test with TWO-SIDED timing: pair A must be expired AND pair B still inside the window when
        // the final checks run. A stalled host (thread-pool starvation while other tests run in parallel) stretched the sleeps
        // to 5 s instead of 3.3 s once, which expired pair B too, and the (correct) limiter then allowed one request too many.
        // So each attempt measures its own timings, and if scheduling was too jittery for the scenario to be valid it is
        // repeated rather than reporting a limiter bug that is not there. A real limiter bug fails every valid attempt.
        const int safetyMs = 100;
        const int maxAttempts = 4;
        var observed = new List<string>();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var limiter = CreateLimiter(limit: 4, windowSeconds: 3);
            var s = Supplier();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            (await limiter.TryAcquireAsync(s)).Allowed.Should().BeTrue();
            (await limiter.TryAcquireAsync(s)).Allowed.Should().BeTrue();
            var pairAEnd = clock.ElapsedMilliseconds;                       // pair A expires by pairAEnd + 3000 at the latest

            await Task.Delay(1600);
            var pairBStart = clock.ElapsedMilliseconds;                     // pair B expires no earlier than pairBStart + 3000
            (await limiter.TryAcquireAsync(s)).Allowed.Should().BeTrue();
            (await limiter.TryAcquireAsync(s)).Allowed.Should().BeTrue();
            var fifth = await limiter.TryAcquireAsync(s);
            var fifthAt = clock.ElapsedMilliseconds;

            await Task.Delay(1700);
            var checksStart = clock.ElapsedMilliseconds;
            var first = await limiter.TryAcquireAsync(s);
            var second = await limiter.TryAcquireAsync(s);
            var third = await limiter.TryAcquireAsync(s);
            var checksEnd = clock.ElapsedMilliseconds;

            var validScenario =
                fifthAt < 3000 - safetyMs                                   // pair A still inside the window at the 5th request
                && checksStart >= pairAEnd + 3000 + safetyMs                // pair A has certainly expired when the checks start
                && checksEnd < pairBStart + 3000 - safetyMs;                // pair B has certainly not expired when they end

            if (!validScenario)
            {
                observed.Add($"attempt {attempt}: 5th at {fifthAt} ms, checks {checksStart}-{checksEnd} ms (pair A ends {pairAEnd}, pair B starts {pairBStart})");
                continue;                                                   // the host stalled: not a limiter result, try again
            }

            fifth.Allowed.Should().BeFalse("pair A and pair B fill the window of 4");
            first.Allowed.Should().BeTrue("pair A expired, freeing exactly its two slots");
            second.Allowed.Should().BeTrue("pair A expired, freeing exactly its two slots");
            third.Allowed.Should().BeFalse("pair B is still inside the window, so a fixed bucket would wrongly allow more");
            return;
        }

        throw new Xunit.Sdk.XunitException(
            $"The host was too loaded to run this timing test: no attempt kept its timings within the scenario's margins. {string.Join("; ", observed)}");
    }

    [Fact]
    public async Task Denied_requests_do_not_consume_quota()
    {
        var limiter = CreateLimiter(limit: 2, windowSeconds: 2);
        var s = Supplier();
        await limiter.TryAcquireAsync(s); await limiter.TryAcquireAsync(s);
        for (var i = 0; i < 20; i++) (await limiter.TryAcquireAsync(s)).Allowed.Should().BeFalse();

        await Task.Delay(2300);

        // had denials been recorded, the window would still be full
        (await limiter.TryAcquireAsync(s)).Allowed.Should().BeTrue();
        (await limiter.TryAcquireAsync(s)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Suppliers_are_strictly_isolated()
    {
        var limiter = CreateLimiter(limit: 5, windowSeconds: 60);
        string a = Supplier(), b = Supplier();
        for (var i = 0; i < 5; i++) await limiter.TryAcquireAsync(a);
        (await limiter.TryAcquireAsync(a)).Allowed.Should().BeFalse();

        for (var i = 0; i < 5; i++) (await limiter.TryAcquireAsync(b)).Allowed.Should().BeTrue();
        (await limiter.TryAcquireAsync(b)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Prefix_like_supplier_ids_do_not_collide()
    {
        var limiter = CreateLimiter(limit: 2, windowSeconds: 60);
        var s = Supplier();
        await limiter.TryAcquireAsync(s); await limiter.TryAcquireAsync(s);
        (await limiter.TryAcquireAsync(s)).Allowed.Should().BeFalse();
        (await limiter.TryAcquireAsync(s + "2")).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_requests_never_exceed_the_limit()
    {
        var limiter = CreateLimiter(limit: 100, windowSeconds: 60);
        var s = Supplier();

        var results = await Task.WhenAll(Enumerable.Range(0, 300).Select(_ => Task.Run(() => limiter.TryAcquireAsync(s))));

        results.Count(r => r.Allowed).Should().Be(100);
        results.Count(r => !r.Allowed).Should().Be(200);
    }
}

/// <summary>
/// Extra contract for limiters whose state is shared across app instances (Redis, a database, ...).
/// An in-process limiter must NOT derive from this: it cannot satisfy it, by design.
/// </summary>
public abstract class DistributedRateLimiterContract : RateLimiterContract
{
    /// <summary>
    /// <paramref name="count"/> limiters that share ONE backing state, exactly as separate app instances would
    /// (separate connections/objects, same store and keys).
    /// </summary>
    protected abstract IReadOnlyList<IRateLimiter> CreateSharedLimiters(int limit, int windowSeconds, int count);

    protected sealed override IRateLimiter CreateLimiter(int limit, int windowSeconds) =>
        CreateSharedLimiters(limit, windowSeconds, 1)[0];

    [Fact]
    public async Task Concurrent_requests_across_instances_never_exceed_the_limit()
    {
        var instances = CreateSharedLimiters(limit: 100, windowSeconds: 60, count: 2);
        var s = Supplier();

        var results = await Task.WhenAll(Enumerable.Range(0, 300)
            .Select(i => Task.Run(() => instances[i % 2].TryAcquireAsync(s))));

        results.Count(r => r.Allowed).Should().Be(100);
        results.Count(r => !r.Allowed).Should().Be(200);
    }

    [Fact]
    public async Task State_is_shared_across_instances()
    {
        var instances = CreateSharedLimiters(limit: 5, windowSeconds: 60, count: 2);
        var s = Supplier();

        for (var i = 0; i < 5; i++) (await instances[0].TryAcquireAsync(s)).Allowed.Should().BeTrue();

        (await instances[1].TryAcquireAsync(s)).Allowed.Should().BeFalse(); // the other instance sees the full window
    }
}
