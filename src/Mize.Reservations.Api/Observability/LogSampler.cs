using System.Collections.Concurrent;

namespace Mize.Reservations.Api.Observability;

/// <summary>
/// Lets a repeating event be logged at most once per interval per key, and reports how many were suppressed in between.
/// A throttle flood or a Redis outage can produce thousands of identical events per second: logging every one would bury the
/// signal and cost more than the work itself. The counts are not lost: metrics record every event, and the next line that is
/// logged says how many were skipped.
/// </summary>
public sealed class LogSampler(TimeProvider? time = null)
{
    private const int PruneThreshold = 10_000;

    private sealed class Entry
    {
        public readonly object Gate = new();
        public long LastLoggedAt;      // TimeProvider timestamp
        public long Suppressed;
    }

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>How many keys are being tracked (bounded: quiet keys are pruned once the table grows past a threshold).</summary>
    public int TrackedKeys => _entries.Count;

    /// <summary>True when this event should be logged now; <paramref name="suppressed"/> is how many were skipped since the last logged one.</summary>
    public bool ShouldLog(string key, TimeSpan interval, out long suppressed)
    {
        var now = _time.GetTimestamp();
        var entry = _entries.GetOrAdd(key, _ => new Entry { LastLoggedAt = long.MinValue });

        lock (entry.Gate)
        {
            if (entry.LastLoggedAt == long.MinValue || _time.GetElapsedTime(entry.LastLoggedAt, now) >= interval)
            {
                suppressed = entry.Suppressed;
                entry.Suppressed = 0;
                entry.LastLoggedAt = now;
                if (_entries.Count > PruneThreshold) Prune(now, interval);
                return true;
            }

            entry.Suppressed++;
            suppressed = 0;
            return false;
        }
    }

    // keeps the table bounded when keys are unbounded (supplier ids): drops keys quiet for two intervals
    private void Prune(long now, TimeSpan interval)
    {
        foreach (var (key, entry) in _entries)
            if (entry.Suppressed == 0 && _time.GetElapsedTime(entry.LastLoggedAt, now) >= interval * 2)
                _entries.TryRemove(key, out _);
    }
}
