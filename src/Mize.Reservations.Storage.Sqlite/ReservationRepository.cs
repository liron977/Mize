using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;

namespace Mize.Reservations.Storage.Sqlite;

/// <summary>
/// SQLite adapter for <see cref="IReservationRepository"/>. It owns only persistence: it loads the current row,
/// asks <see cref="ReservationDecider"/> what to do (the business rules live in Core, once), and applies the action.
/// Writes are queued on the group-commit <see cref="WriteBatcher"/> and complete after the batch commits.
/// </summary>
public sealed class ReservationRepository(SqliteConnectionFactory factory, WriteBatcher batcher) : IReservationRepository
{
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    // Once queued, the write is not cancelled by a client disconnect: it is idempotent and the supplier retries.
    public Task<IngestOutcome> UpsertAsync(Reservation reservation, CancellationToken ct = default) =>
        batcher.EnqueueUpsert(reservation);

    public async Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var row = await SelectRowAsync(conn, supplierId, reservationId);
        return row is null ? null : ToReservation(supplierId, reservationId, row);
    }

    private sealed class Row
    {
        public string RoomId { get; set; } = "";
        public string CheckIn { get; set; } = "";
        public string CheckOut { get; set; } = "";
        public long PriceCents { get; set; }
        public string UpdatedAtUtc { get; set; } = "";
    }

    /// <summary>
    /// Applies one reservation on an already-open write transaction (the batch's). Does NOT touch counters;
    /// the batcher aggregates them from the returned outcome.
    /// </summary>
    internal static async Task<IngestOutcome> ApplyAsync(SqliteConnection conn, Reservation r)
    {
        var row = await SelectRowAsync(conn, r.SupplierId, r.ReservationId);
        var existing = row is null ? null : ToReservation(r.SupplierId, r.ReservationId, row);

        var decision = ReservationDecider.Decide(existing, r); // business rules: Core, not here

        var p = new
        {
            r.SupplierId,
            r.ReservationId,
            r.RoomId,
            CheckIn = Format(r.CheckInUtc),
            CheckOut = Format(r.CheckOutUtc),
            r.PriceCents,
            UpdatedAtUtc = Format(r.UpdatedAtUtc),
            Now = Format(DateTime.UtcNow)
        };

        switch (decision.Action)
        {
            case ReservationAction.Insert:
                await conn.ExecuteAsync(
                    """
                    INSERT INTO Reservations (SupplierId, ReservationId, RoomId, CheckIn, CheckOut, PriceCents, UpdatedAtUtc, CreatedAtUtc)
                    VALUES (@SupplierId, @ReservationId, @RoomId, @CheckIn, @CheckOut, @PriceCents, @UpdatedAtUtc, @Now)
                    """, p);
                break;

            case ReservationAction.Update:
                await conn.ExecuteAsync(
                    """
                    UPDATE Reservations
                    SET RoomId = @RoomId, CheckIn = @CheckIn, CheckOut = @CheckOut,
                        PriceCents = @PriceCents, UpdatedAtUtc = @UpdatedAtUtc
                    WHERE SupplierId = @SupplierId AND ReservationId = @ReservationId
                    """, p);
                break;

            case ReservationAction.TouchTimestamp:
                await conn.ExecuteAsync(
                    """
                    UPDATE Reservations SET UpdatedAtUtc = @UpdatedAtUtc
                    WHERE SupplierId = @SupplierId AND ReservationId = @ReservationId
                    """, p);
                break;

            case ReservationAction.None:
                break;
        }

        return decision.Outcome;
    }

    /// <summary>
    /// Deletes the row only if it is still EXACTLY the given version (every detail and the timestamp). A supplier may have
    /// updated it since it was archived; in that case nothing is deleted and 0 is returned.
    /// </summary>
    internal static async Task<int> DeleteIfUnchangedAsync(SqliteConnection conn, Reservation r) =>
        await conn.ExecuteAsync(
            """
            DELETE FROM Reservations
            WHERE SupplierId = @SupplierId AND ReservationId = @ReservationId
              AND RoomId = @RoomId AND CheckIn = @CheckIn AND CheckOut = @CheckOut
              AND PriceCents = @PriceCents AND UpdatedAtUtc = @UpdatedAtUtc
            """,
            new
            {
                r.SupplierId, r.ReservationId, r.RoomId,
                CheckIn = Format(r.CheckInUtc), CheckOut = Format(r.CheckOutUtc),
                r.PriceCents, UpdatedAtUtc = Format(r.UpdatedAtUtc)
            });

    private static Task<Row?> SelectRowAsync(SqliteConnection conn, string supplierId, string reservationId) =>
        conn.QuerySingleOrDefaultAsync<Row>(
            """
            SELECT RoomId, CheckIn, CheckOut, PriceCents, UpdatedAtUtc
            FROM Reservations WHERE SupplierId = @supplierId AND ReservationId = @reservationId
            """, new { supplierId, reservationId });

    internal static string Format(DateTime utc) => utc.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static Reservation ToReservation(string supplierId, string reservationId, Row row) => new(
        supplierId, reservationId, row.RoomId,
        Parse(row.CheckIn), Parse(row.CheckOut), row.PriceCents, Parse(row.UpdatedAtUtc));

    private static DateTime Parse(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // The column name comes from the StatsCounter enum only (never user input); enum names are the column names.
    internal static Task BumpAsync(SqliteConnection conn, string supplierId, StatsCounter counter, long n = 1)
    {
        var column = counter.ToString();
        return conn.ExecuteAsync(
            $"""
            INSERT INTO SupplierStats (SupplierId, {column}) VALUES (@supplierId, @n)
            ON CONFLICT(SupplierId) DO UPDATE SET {column} = {column} + @n
            """, new { supplierId, n });
    }
}
