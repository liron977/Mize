using Mize.Reservations.Core.Abstractions;
using System.Diagnostics;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Telemetry;

namespace Mize.Reservations.Core.Services;

public sealed class IngestService(
    IRateLimiter rateLimiter,
    IReservationRepository reservations,
    IStatsRepository stats,
    RetentionPolicy? retention = null,
    TimeProvider? time = null,
    ReservationMetrics? metrics = null)
{
    private readonly RetentionPolicy _retention = retention ?? RetentionPolicy.Disabled;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<IngestResult> IngestAsync(IngestCommand cmd, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await IngestCoreAsync(cmd, ct);
            metrics?.RecordIngest(result.Outcome, Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch
        {
            // limiter or storage unavailable (HTTP 503) or an unexpected failure: still counted, with its own outcome
            metrics?.RecordIngest("error", Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    private async Task<IngestResult> IngestCoreAsync(IngestCommand cmd, CancellationToken ct)
    {
        // The supplier is needed first: it is the rate-limit key.
        var supplier = SupplierKey.Normalize(cmd.SupplierId);
        if (supplier.Length == 0)
        {
            // No usable supplier means no rate-limit key, so nothing else can happen. Report everything the adapter already knows
            // (a body that is not an object, a supplierId of the wrong type, other unreadable fields) and only add "required" when
            // nothing explains the missing supplier.
            var explained = cmd.FormatErrors is null ? new Dictionary<string, string[]>() : Copy(cmd.FormatErrors);
            if (!explained.ContainsKey("body") && !explained.ContainsKey("supplierId"))
                explained["supplierId"] = ["supplierId is required."];
            return new IngestResult(IngestOutcome.Invalid, explained);
        }

        // It is about to become a rate-limit key (a Redis key): never let an oversized or unsafe id get that far.
        if (IngestLimits.IdentifierProblem(supplier, IngestLimits.MaxSupplierIdLength) is { } supplierProblem)
            return Invalid("supplierId", "supplierId " + supplierProblem);

        // Rate limit BEFORE any other work, so every request that reaches us costs quota.
        var decision = await rateLimiter.TryAcquireAsync(supplier, ct);
        if (!decision.Allowed)
        {
            await stats.IncrementThrottledAsync(supplier, ct);
            return new IngestResult(IngestOutcome.Throttled, RetryAfter: decision.RetryAfter);
        }

        var (reservation, errors) = IngestValidator.Validate(cmd, supplier);

        // Report everything at once: what the adapter could not read, plus what the business rules reject.
        if (cmd.FormatErrors is { Count: > 0 } formatErrors)
        {
            var merged = Copy(formatErrors);
            foreach (var (field, messages) in errors) merged.TryAdd(field, messages);
            return new IngestResult(IngestOutcome.Invalid, merged);
        }

        if (reservation is null)
            return new IngestResult(IngestOutcome.Invalid, errors);

        // Resurrection guard: once stays older than the retention cutoff can be purged, updates for them must be ignored,
        // otherwise a late retry would be stored as a brand-new reservation. A 200 (not an error), so suppliers don't retry.
        if (_retention.IsExpired(reservation, _time.GetUtcNow().UtcDateTime))
            return new IngestResult(IngestOutcome.Expired);

        var outcome = await reservations.UpsertAsync(reservation, ct);
        return new IngestResult(outcome);
    }

    private static Dictionary<string, string[]> Copy(IReadOnlyDictionary<string, string[]> source) =>
        source.ToDictionary(kv => kv.Key, kv => kv.Value);

    private static IngestResult Invalid(string field, string message) =>
        new(IngestOutcome.Invalid, new Dictionary<string, string[]> { [field] = [message] });
}
