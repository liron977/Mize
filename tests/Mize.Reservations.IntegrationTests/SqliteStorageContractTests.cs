using Mize.Reservations.Contracts;
using Mize.Reservations.Storage.Sqlite;

namespace Mize.Reservations.IntegrationTests;

/// <summary>Runs the engine-agnostic storage contract against the SQLite adapter.</summary>
public abstract class SqliteStorageContractBase(int maxBatchSize) : StorageContract
{
    protected override async Task<StorageUnderTest> CreateAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mize-contract-" + Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(Path.Combine(dir, "t.db"));
        await new SqliteStorageInitializer(factory).InitializeAsync();

        var batcher = new WriteBatcher(factory, new WriteBatcherOptions { MaxBatchSize = maxBatchSize });
        batcher.Start();

        return new StorageUnderTest(
            new ReservationRepository(factory, batcher),
            new StatsRepository(factory, batcher),
            async () =>
            {
                await batcher.DisposeAsync();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
            });
    }
}

/// <summary>Group commit on (the default).</summary>
public sealed class SqliteStorageContractTests() : SqliteStorageContractBase(maxBatchSize: 256);

/// <summary>Group commit off (one commit per request): the contract must hold in both modes.</summary>
public sealed class SqliteStorageNoBatchingContractTests() : SqliteStorageContractBase(maxBatchSize: 1);
