using Mize.Reservations.Contracts;
using Mize.Reservations.Storage.InMemory;

namespace Mize.Reservations.UnitTests;

/// <summary>
/// Verification that the storage ports are engine-neutral: a second, completely different engine (no SQL, no files,
/// written against Core only) passes the very same contract suite as SQLite. If a port were SQLite-shaped, this is
/// where it would show.
/// </summary>
public sealed class InMemoryStorageContractTests : StorageContract
{
    protected override Task<StorageUnderTest> CreateAsync()
    {
        var store = new InMemoryStore(); // both repositories share the one store, as the SQLite pair shares one database
        return Task.FromResult(new StorageUnderTest(new InMemoryReservationRepository(store), new InMemoryStatsRepository(store)));
    }
}
