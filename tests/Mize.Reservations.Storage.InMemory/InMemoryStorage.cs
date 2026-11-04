using Microsoft.Extensions.DependencyInjection;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;

namespace Mize.Reservations.Storage.InMemory;

// =====================================================================================================================
// VERIFICATION ONLY. This adapter exists to prove that the storage ports in Core are engine-neutral and swappable:
// it depends on Core and nothing else, takes every business decision from ReservationDecider, and passes the same
// StorageContract as SQLite. It is NOT durable, NOT shared across instances, lives under tests/, is not referenced by
// the Api and is not selectable via Storage:Provider. The supported storage engine of this project is SQLite.
// =====================================================================================================================

/// <summary>
/// The in-memory "engine": one lock guards both the reservations and the counters, so an upsert and its counter bump
/// are atomic together. Both repositories below must share ONE store, the same coupling the SQLite adapter has
/// (the counter must be written with the reservation), made explicit here.
/// </summary>
public sealed class InMemoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Supplier, string Id), Reservation> _reservations = new();
    private readonly Dictionary<string, long[]> _counters = new(); // indexed by (int)StatsCounter

    internal IngestOutcome Upsert(Reservation incoming)
    {
        lock (_gate)
        {
            var key = (incoming.SupplierId, incoming.ReservationId);
            _reservations.TryGetValue(key, out var existing);

            var decision = ReservationDecider.Decide(existing, incoming); // the rules live in Core, not here

            switch (decision.Action)
            {
                case ReservationAction.Insert:
                case ReservationAction.Update:
                    _reservations[key] = incoming;
                    break;
                case ReservationAction.TouchTimestamp:
                    _reservations[key] = existing! with { UpdatedAtUtc = incoming.UpdatedAtUtc };
                    break;
                case ReservationAction.None:
                    break;
            }

            Bump(incoming.SupplierId, decision.Outcome.ToCounter());
            return decision.Outcome;
        }
    }

    internal Reservation? Get(string supplierId, string reservationId)
    {
        lock (_gate) return _reservations.GetValueOrDefault((supplierId, reservationId));
    }

    internal void BumpThrottled(string supplierId)
    {
        lock (_gate) Bump(supplierId, StatsCounter.Throttled);
    }

    internal SupplierStats Stats(string supplierId)
    {
        lock (_gate)
        {
            if (!_counters.TryGetValue(supplierId, out var c)) return new SupplierStats(supplierId, 0, 0, 0, 0);
            return new SupplierStats(supplierId,
                Ingested: c[(int)StatsCounter.Ingested],
                Throttled: c[(int)StatsCounter.Throttled],
                Unchanged: c[(int)StatsCounter.Unchanged],
                Stale: c[(int)StatsCounter.Stale]);
        }
    }

    internal IReadOnlyList<Reservation> GetExpired(DateTime cutoffUtc, int limit, Reservation? after)
    {
        lock (_gate)
        {
            return _reservations.Values
                .Where(r => r.CheckOutUtc < cutoffUtc && (after is null || Compare(r, after) > 0))
                .OrderBy(r => r, Comparer<Reservation>.Create(Compare))
                .Take(limit)
                .ToList();
        }
    }

    internal int DeleteIfUnchanged(IReadOnlyList<Reservation> archived)
    {
        lock (_gate)
        {
            var deleted = 0;
            foreach (var row in archived)
            {
                var key = (row.SupplierId, row.ReservationId);
                // record equality = every detail and the timestamp: delete only the exact archived version
                if (_reservations.TryGetValue(key, out var current) && current == row && _reservations.Remove(key)) deleted++;
            }
            return deleted;
        }
    }

    // (check-out, supplier, reservation id), the order the retention port promises
    private static int Compare(Reservation a, Reservation b)
    {
        var c = a.CheckOutUtc.CompareTo(b.CheckOutUtc);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.SupplierId, b.SupplierId);
        return c != 0 ? c : string.CompareOrdinal(a.ReservationId, b.ReservationId);
    }

    // caller holds the lock
    private void Bump(string supplierId, StatsCounter counter)
    {
        if (!_counters.TryGetValue(supplierId, out var c))
            _counters[supplierId] = c = new long[Enum.GetValues<StatsCounter>().Length];
        c[(int)counter]++;
    }
}

public sealed class InMemoryReservationRepository(InMemoryStore store) : IReservationRepository
{
    public Task<IngestOutcome> UpsertAsync(Reservation reservation, CancellationToken ct = default) =>
        Task.FromResult(store.Upsert(reservation));

    public Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken ct = default) =>
        Task.FromResult(store.Get(supplierId, reservationId));
}

public sealed class InMemoryStatsRepository(InMemoryStore store) : IStatsRepository
{
    public Task IncrementThrottledAsync(string supplierId, CancellationToken ct = default)
    {
        store.BumpThrottled(supplierId);
        return Task.CompletedTask;
    }

    public Task<SupplierStats> GetAsync(string supplierId, CancellationToken ct = default) =>
        Task.FromResult(store.Stats(supplierId));
}

public sealed class InMemoryRetentionStore(InMemoryStore store) : IReservationRetentionStore
{
    public Task<IReadOnlyList<Reservation>> GetExpiredAsync(DateTime cutoffUtc, int limit, Reservation? after = null, CancellationToken ct = default) =>
        Task.FromResult(store.GetExpired(cutoffUtc, limit, after));

    public Task<int> DeleteIfUnchangedAsync(IReadOnlyList<Reservation> archived, CancellationToken ct = default) =>
        Task.FromResult(store.DeleteIfUnchanged(archived));

    public Task ReclaimSpaceAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Nothing to set up: there is no schema.</summary>
public sealed class InMemoryStorageInitializer : IStorageInitializer
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public static class InMemoryStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the verification-only in-memory storage. It is deliberately NOT wired into the Api's
    /// <c>InfrastructureRegistration</c>; tests opt in by replacing the storage ports (see the fully-swapped API tests).
    /// </summary>
    public static IServiceCollection AddInMemoryStorage(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryStore>();
        services.AddSingleton<IReservationRepository, InMemoryReservationRepository>();
        services.AddSingleton<IStatsRepository, InMemoryStatsRepository>();
        services.AddSingleton<IReservationRetentionStore, InMemoryRetentionStore>();
        services.AddSingleton<IStorageInitializer, InMemoryStorageInitializer>();
        return services;
    }
}
