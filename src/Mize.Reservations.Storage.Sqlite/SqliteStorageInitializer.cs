using Mize.Reservations.Core.Abstractions;

namespace Mize.Reservations.Storage.Sqlite;

/// <summary>Creates the schema. Idempotent; safe to run concurrently from several containers.</summary>
public sealed class SqliteStorageInitializer(SqliteConnectionFactory factory) : IStorageInitializer
{
    public Task InitializeAsync(CancellationToken ct = default) => DbInitializer.InitializeAsync(factory, ct);
}
