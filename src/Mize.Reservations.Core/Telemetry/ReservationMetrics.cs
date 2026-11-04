using System.Diagnostics.Metrics;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Core.Telemetry;

/// <summary>
/// The service's metrics, as standard .NET <see cref="Meter"/> instruments (part of the framework, so Core needs no package
/// and no vendor). A host decides where they go by attaching an exporter (the API exposes them for Prometheus); with no
/// exporter attached, recording costs almost nothing.
///
/// Cardinality: no instrument is tagged by supplier, because supplier ids are unbounded and a metric series per supplier
/// would grow without limit. Per-supplier numbers are in the durable stats endpoint, and a throttled supplier is named in
/// a (sampled) log line.
/// </summary>
public sealed class ReservationMetrics : IDisposable
{
    public const string MeterName = "Mize.Reservations";

    // instrument names (stable: dashboards and alerts depend on them)
    public const string IngestRequestsName = "reservations.ingest.requests";
    public const string IngestDurationName = "reservations.ingest.duration";
    public const string RateLimitDecisionsName = "reservations.ratelimit.decisions";
    public const string StorageRejectionsName = "reservations.storage.rejections";
    public const string BatchSizeName = "reservations.storage.batch.size";
    public const string BatchCommitDurationName = "reservations.storage.batch.commit.duration";
    public const string QueueDepthName = "reservations.storage.queue.depth";

    /// <summary>Values of the <c>result</c> tag on <see cref="RateLimitDecisionsName"/>.</summary>
    public static class RateLimitResult
    {
        public const string Allowed = "allowed";
        public const string Denied = "denied";
        public const string BlockedLocally = "blocked_locally"; // rejected by the local block cache, no store call
        public const string Unavailable = "unavailable";        // the shared store could not be reached
    }

    /// <summary>Values of the <c>reason</c> tag on <see cref="StorageRejectionsName"/>.</summary>
    public static class RejectionReason
    {
        public const string QueueFull = "queue_full";
        public const string Busy = "busy";
    }

    private readonly Meter _meter;
    private readonly Counter<long> _ingestRequests;
    private readonly Histogram<double> _ingestDuration;
    private readonly Counter<long> _rateLimitDecisions;
    private readonly Counter<long> _storageRejections;
    private readonly Histogram<long> _batchSize;
    private readonly Histogram<double> _batchCommitDuration;
    private Func<int>? _queueDepth;

    /// <param name="meterFactory">The host's meter factory (so a host or a test owns the meter's lifetime); null creates a private meter.</param>
    public ReservationMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);

        _ingestRequests = _meter.CreateCounter<long>(IngestRequestsName, "{request}",
            "Ingest requests by outcome: created, updated, unchanged, stale, expired, throttled, invalid, error.");
        _ingestDuration = _meter.CreateHistogram<double>(IngestDurationName, "s",
            "Time to process an ingest request (including waiting for the group commit), by outcome.");
        _rateLimitDecisions = _meter.CreateCounter<long>(RateLimitDecisionsName, "{decision}",
            "Rate-limit decisions: allowed, denied, blocked_locally (no store call), unavailable (store down).");
        _storageRejections = _meter.CreateCounter<long>(StorageRejectionsName, "{request}",
            "Writes refused by storage: queue_full (bounded queue) or busy (database lock timeout).");
        _batchSize = _meter.CreateHistogram<long>(BatchSizeName, "{item}", "Writes committed per group-commit transaction.");
        _batchCommitDuration = _meter.CreateHistogram<double>(BatchCommitDurationName, "s",
            "Time to apply and commit one group-commit batch.");
        _meter.CreateObservableGauge(QueueDepthName, ObserveQueueDepth, "{item}", "Writes waiting in the group-commit queue.");
    }

    public void RecordIngest(string outcome, TimeSpan elapsed)
    {
        var tag = new KeyValuePair<string, object?>("outcome", outcome);
        _ingestRequests.Add(1, tag);
        _ingestDuration.Record(elapsed.TotalSeconds, tag);
    }

    public void RecordIngest(IngestOutcome outcome, TimeSpan elapsed) => RecordIngest(outcome.ToString().ToLowerInvariant(), elapsed);

    public void RecordRateLimit(string result) =>
        _rateLimitDecisions.Add(1, new KeyValuePair<string, object?>("result", result));

    public void RecordStorageRejection(string reason) =>
        _storageRejections.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void RecordBatch(int items, TimeSpan commitDuration)
    {
        _batchSize.Record(items);
        _batchCommitDuration.Record(commitDuration.TotalSeconds);
    }

    /// <summary>The storage adapter says how to read its queue depth; the gauge is read only when a collector asks.</summary>
    public void RegisterQueueDepth(Func<int> reader) => _queueDepth = reader;

    private IEnumerable<Measurement<int>> ObserveQueueDepth()
    {
        var reader = _queueDepth;
        if (reader is not null) yield return new Measurement<int>(reader());
    }

    public void Dispose() => _meter.Dispose();
}
