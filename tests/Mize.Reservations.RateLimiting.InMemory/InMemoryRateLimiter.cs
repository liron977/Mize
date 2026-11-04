using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.RateLimiting.InMemory;

// =====================================================================================================================
// VERIFICATION ONLY. This limiter exists to prove that IRateLimiter is engine-neutral and swappable (it passes the same
// RateLimiterContract as the Redis limiter). It lives under tests/, is not referenced by the Api, and is not selectable
// via RateLimit:Provider. The supported rate-limiting engine of this project is Redis.
// =====================================================================================================================

/// <summary>
/// In-process sliding-window-log limiter: same exact semantics as the Redis one (rolling window, denied requests
/// are not recorded, suppliers isolated), but state lives in this process.
///
/// <b>Single instance only.</b> With N instances each would enforce the limit on its own, allowing up to N x the
/// limit, which is one reason it is not offered in the product.
/// </summary>
public sealed class InMemoryRateLimiter(IOptions<RateLimitOptions> options, TimeProvider? time = null) : IRateLimiter
{
    private const int PruneThreshold = 10_000;

    private readonly RateLimitOptions _o = options.Value;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Window> _windows = new();

    private sealed class Window
    {
        public readonly object Gate = new();
        public readonly Queue<long> Hits = new(); // accepted request times (ms), oldest first
    }

    public Task<RateLimitDecision> TryAcquireAsync(string supplierKey, CancellationToken ct = default)
    {
        var now = NowMs();
        var windowMs = _o.WindowSeconds * 1000L;
        var w = _windows.GetOrAdd(supplierKey, _ => new Window());

        RateLimitDecision decision;
        lock (w.Gate)
        {
            while (w.Hits.Count > 0 && w.Hits.Peek() <= now - windowMs) w.Hits.Dequeue(); // drop expired

            if (w.Hits.Count >= _o.Limit)
            {
                var retryMs = Math.Max(1, w.Hits.Peek() + windowMs - now); // when the oldest entry leaves the window
                decision = new RateLimitDecision(false, TimeSpan.FromMilliseconds(retryMs));
            }
            else
            {
                w.Hits.Enqueue(now); // only accepted requests are recorded
                decision = new RateLimitDecision(true, TimeSpan.Zero);
            }
        }

        if (_windows.Count > PruneThreshold) Prune(now, windowMs);
        return Task.FromResult(decision);
    }

    private long NowMs() => (long)(_time.GetTimestamp() * 1000.0 / _time.TimestampFrequency);

    private void Prune(long now, long windowMs)
    {
        foreach (var (key, w) in _windows)
        {
            lock (w.Gate)
            {
                if (w.Hits.Count == 0 || w.Hits.Peek() <= now - windowMs && w.Hits.ToArray()[^1] <= now - windowMs)
                    _windows.TryRemove(new KeyValuePair<string, Window>(key, w));
            }
        }
    }
}
