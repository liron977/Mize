using Microsoft.Extensions.Options;
using Mize.Reservations.Contracts;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.RateLimiting;
using Mize.Reservations.RateLimiting.InMemory;
using Mize.Reservations.Storage.InMemory;

namespace Mize.Reservations.UnitTests;

/// <summary>
/// Verification-only in-process limiter: satisfies the base contract (proving the port is swappable); it is deliberately
/// NOT a distributed limiter and is not selectable in the product.
/// </summary>
public sealed class InMemoryRateLimiterContractTests : RateLimiterContract
{
    protected override IRateLimiter CreateLimiter(int limit, int windowSeconds) =>
        new InMemoryRateLimiter(Options.Create(new RateLimitOptions { Limit = limit, WindowSeconds = windowSeconds }));
}

/// <summary>
/// The same contract against the verification-only in-memory engine: proves the new retention port is engine-neutral too
/// (it needs no SQL, no index and no file).
/// </summary>
public sealed class InMemoryRetentionStoreContractTests : RetentionStoreContract
{
    protected override Task<StorageUnderTest> CreateAsync()
    {
        var store = new InMemoryStore();
        return Task.FromResult(new StorageUnderTest(
            new InMemoryReservationRepository(store), new InMemoryStatsRepository(store),
            retention: new InMemoryRetentionStore(store)));
    }
}
