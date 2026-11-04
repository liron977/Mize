using Dapper;
using Microsoft.Data.Sqlite;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Storage.Sqlite;

/// <summary>
/// SQLite adapter for <see cref="IReservationRetentionStore"/>. Reads are plain WAL reads (they never block writers);
/// deletes go through the group-commit writer, so they take the write lock in small batches like every other write.
/// </summary>
public sealed class SqliteRetentionStore(SqliteConnectionFactory factory, WriteBatcher batcher) : IReservationRetentionStore
{
    private int _indexEnsured;

    private sealed class Row
    {
        public string SupplierId { get; set; } = "";
        public string ReservationId { get; set; } = "";
        public string RoomId { get; set; } = "";
        public string CheckIn { get; set; } = "";
        public string CheckOut { get; set; } = "";
        public long PriceCents { get; set; }
        public string UpdatedAtUtc { get; set; } = "";
    }

    public async Task<IReadOnlyList<Reservation>> GetExpiredAsync(
        DateTime cutoffUtc, int limit, Reservation? after = null, CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);
        await using var conn = await factory.OpenAsync(ct);

        // Keyset paging over (CheckOut, SupplierId, ReservationId): a range scan of the covering index, no sort.
        var sql = after is null
            ? """
              SELECT SupplierId, ReservationId, RoomId, CheckIn, CheckOut, PriceCents, UpdatedAtUtc
              FROM Reservations WHERE CheckOut < @cutoff
              ORDER BY CheckOut, SupplierId, ReservationId LIMIT @limit
              """
            : """
              SELECT SupplierId, ReservationId, RoomId, CheckIn, CheckOut, PriceCents, UpdatedAtUtc
              FROM Reservations
              WHERE CheckOut < @cutoff
                AND (CheckOut > @aCheckOut
                     OR (CheckOut = @aCheckOut AND (SupplierId > @aSupplier
                         OR (SupplierId = @aSupplier AND ReservationId > @aId))))
              ORDER BY CheckOut, SupplierId, ReservationId LIMIT @limit
              """;

        var rows = await conn.QueryAsync<Row>(sql, new
        {
            cutoff = ReservationRepository.Format(cutoffUtc),
            limit,
            aCheckOut = after is null ? null : ReservationRepository.Format(after.CheckOutUtc),
            aSupplier = after?.SupplierId,
            aId = after?.ReservationId
        });

        return rows.Select(r => new Reservation(r.SupplierId, r.ReservationId, r.RoomId,
            Parse(r.CheckIn), Parse(r.CheckOut), r.PriceCents, Parse(r.UpdatedAtUtc))).ToList();
    }

    public Task<int> DeleteIfUnchangedAsync(IReadOnlyList<Reservation> archived, CancellationToken ct = default) =>
        archived.Count == 0 ? Task.FromResult(0) : batcher.EnqueueDeleteIfUnchanged(archived);

    /// <summary>
    /// After deletes, returns free pages to the file system, in small bounded steps so writers are never blocked for long.
    /// Only databases created with <c>auto_vacuum=INCREMENTAL</c> (new databases) can do this; for an older database it is a
    /// no-op, and a one-time full VACUUM (which blocks writers) is needed to switch it on.
    /// </summary>
    public async Task ReclaimSpaceAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            if (await conn.ExecuteScalarAsync<long>("PRAGMA auto_vacuum;") != 2) return;

            for (var i = 0; i < 20 && await conn.ExecuteScalarAsync<long>("PRAGMA freelist_count;") > 0; i++)
                await conn.ExecuteAsync("PRAGMA incremental_vacuum(5000);");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            // writers are busy: skip, the next run reclaims it
        }
    }

    /// <summary>
    /// The retention index is created on first use, not at startup, so deployments that never enable retention don't pay
    /// for it on every insert. Building it takes the write lock for as long as it takes to scan the table, so on a very
    /// large existing table run the first retention pass during a quiet period.
    /// </summary>
    private async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _indexEnsured, 1) == 1) return;
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            await conn.ExecuteAsync(
                "CREATE INDEX IF NOT EXISTS IX_Reservations_Retention ON Reservations (CheckOut, SupplierId, ReservationId);");
        }
        catch
        {
            Interlocked.Exchange(ref _indexEnsured, 0); // retry next time
            throw;
        }
    }

    private static DateTime Parse(string s) =>
        DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
}
