using System.Collections.Concurrent;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Telemetry;

namespace Mize.Reservations.RateLimiting;

/// <summary>
/// Wraps the authoritative limiter. When it denies a supplier, it also tells us exactly when the supplier's
/// oldest entry leaves the window. Entries only ever expire (they are never removed early), so no slot can
/// free before that moment. We remember it locally and reject that supplier without calling the inner limiter
/// until then, so a throttle flood costs no calls to the shared store.
///
/// Safety: the block is computed from the time BEFORE the inner call was sent (the inner script ran later),
/// and times are truncated, so the local block never outlasts the real one. Anything this lets through
/// still reaches the inner limiter, which decides exactly. It can only skip calls that would be denied.
/// </summary>
public sealed class BlockCachingRateLimiter(IRateLimiter inner, TimeProvider? time = null, ReservationMetrics? metrics = null) : IRateLimiter
{
    private const int PruneThreshold = 10_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, long> _blockedUntilMs = new();

    /// <summary>How many suppliers currently have a local block remembered (bounded: expired entries are pruned past a threshold).</summary>
    public int BlockedSuppliers => _blockedUntilMs.Count;

    public async Task<RateLimitDecision> TryAcquireAsync(string supplierKey, CancellationToken ct = default)
    {
        var t0 = NowMs();

        if (_blockedUntilMs.TryGetValue(supplierKey, out var until))
        {
            if (t0 < until)
            {
                metrics?.RecordRateLimit(ReservationMetrics.RateLimitResult.BlockedLocally);
                return new RateLimitDecision(false, TimeSpan.FromMilliseconds(until - t0));
            }

            _blockedUntilMs.TryRemove(new KeyValuePair<string, long>(supplierKey, until)); // expired
        }

        // If this throws (e.g. the shared store is down) nothing is cached and the exception propagates (fail closed).
        RateLimitDecision decision;
        try
        {
            decision = await inner.TryAcquireAsync(supplierKey, ct).ConfigureAwait(false);
        }
        catch (RateLimiterUnavailableException)
        {
            metrics?.RecordRateLimit(ReservationMetrics.RateLimitResult.Unavailable);
            throw;
        }

        metrics?.RecordRateLimit(decision.Allowed ? ReservationMetrics.RateLimitResult.Allowed : ReservationMetrics.RateLimitResult.Denied);

        if (!decision.Allowed)
        {
            var blockedUntil = t0 + (long)decision.RetryAfter.TotalMilliseconds; // truncate: err toward asking the inner limiter
            _blockedUntilMs.AddOrUpdate(supplierKey, blockedUntil, (_, old) => Math.Max(old, blockedUntil));
            if (_blockedUntilMs.Count > PruneThreshold) Prune(t0);
        }

        return decision;
    }

    private long NowMs() => (long)(_time.GetTimestamp() * 1000.0 / _time.TimestampFrequency);

    private void Prune(long nowMs)
    {
        foreach (var (key, until) in _blockedUntilMs)
            if (until <= nowMs)
                _blockedUntilMs.TryRemove(new KeyValuePair<string, long>(key, until));
    }
}
