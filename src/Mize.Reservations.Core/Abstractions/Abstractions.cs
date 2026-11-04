using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Core.Abstractions;

/// <summary>
/// Port: reservation persistence.
/// <para>
/// Contract for implementers: <see cref="UpsertAsync"/> must be atomic per (supplier, reservation) and safe under
/// concurrency across instances, must take its decision from <see cref="ReservationDecider"/> (never re-implement
/// the rules), and must bump the matching <see cref="StatsCounter"/> durably with the write. A task completes only
/// after the change is durable. Any implementation must pass the shared suite in
/// <c>Mize.Reservations.Contracts.StorageContract</c>.
/// </para>
/// </summary>
public interface IReservationRepository
{
    /// <summary>
    /// Atomically creates/updates the reservation (or detects unchanged/stale) and bumps the
    /// matching supplier counter in the same transaction. Safe across multiple instances.
    /// </summary>
    Task<IngestOutcome> UpsertAsync(Reservation reservation, CancellationToken ct = default);

    /// <summary>Reads one reservation, or null if it does not exist.</summary>
    Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken ct = default);
}

/// <summary>
/// Port: one-time storage setup (schema/migrations). Idempotent and safe to run concurrently from several
/// instances. The host runs every registered initializer at startup, so it never names a storage engine.
/// </summary>
public interface IStorageInitializer
{
    Task InitializeAsync(CancellationToken ct = default);
}

/// <summary>Port: per-supplier counters. Increments must be exact (no lost or double counts) and durable once acknowledged.</summary>
public interface IStatsRepository
{
    Task IncrementThrottledAsync(string supplierId, CancellationToken ct = default);
    Task<SupplierStats> GetAsync(string supplierId, CancellationToken ct = default);
}

/// <summary>
/// Port: per-supplier rolling-window rate limiter. Must be exact (never admit more than the limit in any window),
/// must NOT record denied requests, must isolate suppliers, and, for distributed implementations, must hold across
/// instances. Any implementation must pass <c>Mize.Reservations.Contracts.RateLimiterContract</c>
/// (and <c>DistributedRateLimiterContract</c> if it is shared across instances).
/// </summary>
public interface IRateLimiter
{
    /// <summary>Rolling-window check for one supplier. Throws <see cref="RateLimiterUnavailableException"/> if the backing store is down.</summary>
    Task<RateLimitDecision> TryAcquireAsync(string supplierKey, CancellationToken ct = default);
}

public sealed class RateLimiterUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class StorageBusyException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Port: the storage side of retention. Implemented by the storage adapter, since only it can select and delete
/// atomically. Contract for implementers: <see cref="DeleteIfUnchangedAsync"/> must delete a row ONLY if it is still
/// exactly the archived version (all details and the timestamp), because a supplier may update a row between archiving
/// and deleting; and a task completes only after the change is durable. Pass <c>RetentionStoreContract</c>.
/// </summary>
public interface IReservationRetentionStore
{
    /// <summary>
    /// Reservations whose check-out is strictly before the cutoff, ordered by (check-out, supplier, reservation id) and
    /// strictly after <paramref name="after"/> (the last row of the previous page; null for the first page). This order lets an
    /// index on those columns serve the query as a range scan with no sort.
    /// </summary>
    Task<IReadOnlyList<Reservation>> GetExpiredAsync(DateTime cutoffUtc, int limit, Reservation? after = null, CancellationToken ct = default);

    /// <summary>Deletes each reservation only if it is still identical to the given (archived) version. Returns how many were deleted.</summary>
    Task<int> DeleteIfUnchangedAsync(IReadOnlyList<Reservation> archived, CancellationToken ct = default);

    /// <summary>Returns freed space to the file system where the engine supports it; a no-op otherwise.</summary>
    Task ReclaimSpaceAsync(CancellationToken ct = default);
}

/// <summary>
/// Port: cold storage for expired reservations. Contract for implementers: <see cref="WriteAsync"/> returns only when the
/// object is durable (the caller deletes locally right after), writing the same key twice replaces it with identical
/// content (idempotent), <see cref="ReadAsync"/> returns exactly what was written (and throws <see cref="KeyNotFoundException"/>
/// for a missing key), and a key can never resolve outside the archive root. Pass <c>ArchiveContract</c>.
/// </summary>
public interface IReservationArchive
{
    Task WriteAsync(ArchiveBatch batch, CancellationToken ct = default);
    Task<IReadOnlyList<Reservation>> ReadAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);
}