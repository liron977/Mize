using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Core.Services;

public static class IngestValidator
{
    /// <summary>Validates everything except supplierId (which is checked earlier, before rate limiting).</summary>
    public static (Reservation? Reservation, Dictionary<string, string[]> Errors) Validate(
        IngestCommand cmd, string normalizedSupplierId)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string field, string message) => errors[field] = [message];

        // A field the adapter could not read (wrong JSON type, bad timestamp format) already has its own, more useful error.
        bool Reported(string field) => cmd.FormatErrors?.ContainsKey(field) == true;

        var reservationId = cmd.ReservationId?.Trim();
        var roomId = cmd.RoomId?.Trim();

        if (string.IsNullOrEmpty(reservationId)) { if (!Reported("reservationId")) Add("reservationId", "reservationId is required."); }
        else if (IngestLimits.IdentifierProblem(reservationId, IngestLimits.MaxReservationIdLength) is { } rp) Add("reservationId", "reservationId " + rp);

        if (string.IsNullOrEmpty(roomId)) { if (!Reported("roomId")) Add("roomId", "roomId is required."); }
        else if (IngestLimits.IdentifierProblem(roomId, IngestLimits.MaxRoomIdLength) is { } op) Add("roomId", "roomId " + op);

        if (cmd.CheckIn is null) { if (!Reported("checkIn")) Add("checkIn", "checkIn is required."); }
        else if (YearProblem(cmd.CheckIn.Value) is { } cip) Add("checkIn", "checkIn " + cip);

        if (cmd.CheckOut is null) { if (!Reported("checkOut")) Add("checkOut", "checkOut is required."); }
        else if (YearProblem(cmd.CheckOut.Value) is { } cop) Add("checkOut", "checkOut " + cop);

        if (cmd.UpdatedAtUtc is null) { if (!Reported("updatedAtUtc")) Add("updatedAtUtc", "updatedAtUtc is required."); }
        else if (YearProblem(cmd.UpdatedAtUtc.Value) is { } up) Add("updatedAtUtc", "updatedAtUtc " + up);

        if (!errors.ContainsKey("checkOut") && cmd.CheckIn is { } ci && cmd.CheckOut is { } co && co <= ci)
            Add("checkOut", $"checkOut must be after checkIn (checkIn {ci.UtcDateTime:yyyy-MM-ddTHH:mm:ss}Z, checkOut {co.UtcDateTime:yyyy-MM-ddTHH:mm:ss}Z; compared as UTC instants, an equal time is not after).");

        long priceCents = 0;
        if (cmd.Price is null) { if (!Reported("price")) Add("price", "price is required."); }
        else if (cmd.Price < 0) Add("price", "price must not be negative.");
        else if (Math.Round(cmd.Price.Value, 2) != cmd.Price.Value) Add("price", "price must have at most 2 decimal places.");
        else if (cmd.Price > 1_000_000_000m) Add("price", "price is out of range.");
        else priceCents = (long)(cmd.Price.Value * 100m);

        // Never build a reservation from a missing value, even when its error was already reported elsewhere (a field the adapter
        // could not read has no "required" error here, but it is still missing).
        var incomplete = string.IsNullOrEmpty(reservationId) || string.IsNullOrEmpty(roomId)
                         || cmd.CheckIn is null || cmd.CheckOut is null || cmd.UpdatedAtUtc is null || cmd.Price is null;
        if (errors.Count > 0 || incomplete) return (null, errors);

        return (new Reservation(
            normalizedSupplierId,
            reservationId!,
            roomId!,
            cmd.CheckIn!.Value.UtcDateTime,
            cmd.CheckOut!.Value.UtcDateTime,
            priceCents,
            cmd.UpdatedAtUtc!.Value.UtcDateTime), errors);
    }

    private static string? YearProblem(DateTimeOffset value)
    {
        var year = value.UtcDateTime.Year;
        return year is >= IngestLimits.MinYear and <= IngestLimits.MaxYear
            ? null
            : $"must be a date between the years {IngestLimits.MinYear} and {IngestLimits.MaxYear}.";
    }
}
