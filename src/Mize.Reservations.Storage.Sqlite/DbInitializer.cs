using Dapper;

namespace Mize.Reservations.Storage.Sqlite;

public static class DbInitializer
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS Reservations (
            SupplierId    TEXT NOT NULL,
            ReservationId TEXT NOT NULL,
            RoomId        TEXT NOT NULL,
            CheckIn       TEXT NOT NULL,
            CheckOut      TEXT NOT NULL,
            PriceCents    INTEGER NOT NULL,
            UpdatedAtUtc  TEXT NOT NULL,
            CreatedAtUtc  TEXT NOT NULL,
            PRIMARY KEY (SupplierId, ReservationId)
        );
        CREATE TABLE IF NOT EXISTS SupplierStats (
            SupplierId TEXT PRIMARY KEY,
            Ingested   INTEGER NOT NULL DEFAULT 0,
            Throttled  INTEGER NOT NULL DEFAULT 0,
            Unchanged  INTEGER NOT NULL DEFAULT 0,
            Stale      INTEGER NOT NULL DEFAULT 0
        );
        """;

    /// <summary>Idempotent; safe to run concurrently from several containers.</summary>
    public static async Task InitializeAsync(SqliteConnectionFactory factory, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // Only takes effect on a brand-new database, and only before its first table exists (hence before WAL, which writes
        // the header). Lets retention return freed pages to the file system incrementally. No effect on existing databases.
        await conn.ExecuteAsync("PRAGMA auto_vacuum=INCREMENTAL;");
        await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await conn.ExecuteAsync(Schema);
    }
}
