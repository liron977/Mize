using Mize.Reservations.Archive.FileSystem;
using Mize.Reservations.Contracts;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Storage.InMemory;
using Mize.Reservations.Storage.Sqlite;

namespace Mize.Reservations.IntegrationTests;

/// <summary>The retention-store contract against SQLite (batched writer, as in the product).</summary>
public sealed class SqliteRetentionStoreContractTests : RetentionStoreContract
{
    protected override async Task<StorageUnderTest> CreateAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mize-ret-" + Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(Path.Combine(dir, "t.db"));
        await new SqliteStorageInitializer(factory).InitializeAsync();

        var batcher = new WriteBatcher(factory, new WriteBatcherOptions());
        batcher.Start();

        return new StorageUnderTest(
            new ReservationRepository(factory, batcher),
            new StatsRepository(factory, batcher),
            async () =>
            {
                await batcher.DisposeAsync();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
            },
            retention: new SqliteRetentionStore(factory, batcher));
    }
}

/// <summary>The archive contract against the directory-backed adapter.</summary>
public sealed class FileSystemArchiveContractTests : ArchiveContract
{
    protected override Task<(IReservationArchive Archive, Func<Task>? Cleanup)> CreateAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mize-archive-" + Guid.NewGuid().ToString("N"));
        IReservationArchive archive = new FileSystemReservationArchive(dir);
        Func<Task> cleanup = () =>
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
            return Task.CompletedTask;
        };
        return Task.FromResult<(IReservationArchive, Func<Task>?)>((archive, cleanup));
    }
}
