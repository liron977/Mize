namespace Mize.Reservations.Core.Models;

/// <summary>A reservation as stored. All timestamps are UTC; price is in minor units (cents).</summary>
public sealed record Reservation(
    string SupplierId,
    string ReservationId,
    string RoomId,
    DateTime CheckInUtc,
    DateTime CheckOutUtc,
    long PriceCents,
    DateTime UpdatedAtUtc)
{
    /// <summary>
    /// "Same details" for dedup purposes: room, dates and price. UpdatedAtUtc is deliberately
    /// excluded - a duplicate that only bumps the timestamp is treated as unchanged.
    /// </summary>
    public bool HasSameDetailsAs(Reservation other) =>
        RoomId == other.RoomId &&
        CheckInUtc == other.CheckInUtc &&
        CheckOutUtc == other.CheckOutUtc &&
        PriceCents == other.PriceCents;
}
