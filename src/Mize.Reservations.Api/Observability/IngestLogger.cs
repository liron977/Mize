using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Api.Observability;

/// <summary>
/// Structured logging for the ingest endpoint (source-generated, so there is no cost when a level is off).
///   Debug   one line per request: supplier, reservation, outcome, time. Off by default (it is one line per request).
///   Warning a payload that could not be read at all (malformed), sampled once per 30 s with a count of what was skipped.
///   Warning a supplier being throttled, at most once per supplier per 30 s with a count of what was skipped, because the
///           metrics count every one but only the log can say WHICH supplier (metrics are deliberately not tagged by supplier).
/// </summary>
public sealed partial class IngestLogger(ILogger<IngestLogger> logger, LogSampler sampler)
{
    private static readonly TimeSpan ThrottleLogInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MalformedLogInterval = TimeSpan.FromSeconds(30);

    public void Completed(string? supplierId, string? reservationId, IngestResult result, long startedTimestamp)
    {
        var supplier = supplierId?.Trim().ToLowerInvariant() ?? "";
        var elapsedMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

        Request(supplier, reservationId ?? "", result.Outcome.ToString(), elapsedMs);

        if (result.Outcome == IngestOutcome.Throttled
            && sampler.ShouldLog("throttled:" + supplier, ThrottleLogInterval, out var suppressed))
        {
            SupplierThrottled(supplier, (int)Math.Ceiling(result.RetryAfter?.TotalSeconds ?? 0), suppressed);
        }
    }

    /// <summary>A payload that could not even be read (wrong content type, too large, not JSON, ...). Sampled: a misbehaving client can send thousands.</summary>
    public void Malformed(string reason)
    {
        Rejected(reason);
        if (sampler.ShouldLog("malformed", MalformedLogInterval, out var suppressed))
            MalformedRequests(reason, suppressed);
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug,
        Message = "Ingest {SupplierId}/{ReservationId} -> {Outcome} in {ElapsedMs:F1} ms")]
    private partial void Request(string supplierId, string reservationId, string outcome, double elapsedMs);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Supplier {SupplierId} is being throttled (retry in {RetryAfterSeconds}s); {Suppressed} further throttled requests since the last line were not logged")]
    private partial void SupplierThrottled(string supplierId, int retryAfterSeconds, long suppressed);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Malformed ingest request rejected: {Reason}")]
    private partial void Rejected(string reason);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "Malformed ingest request rejected: {Reason} ({Suppressed} similar rejections since the last line were not logged)")]
    private partial void MalformedRequests(string reason, long suppressed);
}
