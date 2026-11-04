namespace Mize.Reservations.Core.Models;

public enum IngestOutcome
{
    Created,
    Updated,
    Unchanged,
    Stale,
    Throttled,
    Invalid,

    /// <summary>The stay ended before the retention cutoff: ignored (HTTP 200, changed:false), never stored.</summary>
    Expired
}

/// <summary>The per-supplier counters. Enum names are the stable storage keys, so do not rename them casually.</summary>
public enum StatsCounter
{
    Ingested,   // created + updated
    Unchanged,
    Stale,
    Throttled
}

public static class IngestOutcomeExtensions
{
    /// <summary>Which counter an outcome increments. <see cref="IngestOutcome.Invalid"/> counts nothing.</summary>
    public static StatsCounter ToCounter(this IngestOutcome outcome) => outcome switch
    {
        IngestOutcome.Created or IngestOutcome.Updated => StatsCounter.Ingested,
        IngestOutcome.Unchanged => StatsCounter.Unchanged,
        IngestOutcome.Stale => StatsCounter.Stale,
        IngestOutcome.Throttled => StatsCounter.Throttled,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "This outcome has no counter.")
    };
}

public sealed record IngestResult(
    IngestOutcome Outcome,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    TimeSpan? RetryAfter = null)
{
    public bool Changed => Outcome is IngestOutcome.Created or IngestOutcome.Updated;
}

/// <summary>
/// Raw, unvalidated ingest input (nullable so missing fields can be reported). <see cref="FormatErrors"/> are the problems the
/// adapter found while reading the payload (a field of the wrong JSON type, a timestamp that is not ISO 8601 with an explicit
/// offset, ...): such a field arrives here as null, and its error is reported instead of a misleading "required".
/// </summary>
public sealed record IngestCommand(
    string? SupplierId,
    string? ReservationId,
    string? RoomId,
    DateTimeOffset? CheckIn,
    DateTimeOffset? CheckOut,
    decimal? Price,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyDictionary<string, string[]>? FormatErrors = null);

public sealed record SupplierStats(string SupplierId, long Ingested, long Throttled, long Unchanged, long Stale);

public sealed record RateLimitDecision(bool Allowed, TimeSpan RetryAfter);
