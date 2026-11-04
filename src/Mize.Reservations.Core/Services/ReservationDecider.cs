using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Core.Services;

/// <summary>What a storage engine must do to persist the decision.</summary>
public enum ReservationAction
{
    /// <summary>No row exists: insert it.</summary>
    Insert,

    /// <summary>Row exists with different details and the incoming data is same-or-newer: overwrite the details.</summary>
    Update,

    /// <summary>Row exists with identical details but the incoming timestamp is newer: advance ONLY UpdatedAtUtc.</summary>
    TouchTimestamp,

    /// <summary>Nothing to write.</summary>
    None
}

public readonly record struct ReservationDecision(ReservationAction Action, IngestOutcome Outcome);

/// <summary>
/// The dedup/upsert business rules, free of any storage engine. Every <c>IReservationRepository</c> implementation
/// runs this inside its own atomic mechanism (SQL transaction, conditional write, ...) and applies the action, so the
/// rules exist exactly once.
/// </summary>
public static class ReservationDecider
{
    public static ReservationDecision Decide(Reservation? existing, Reservation incoming)
    {
        if (existing is null)
            return new(ReservationAction.Insert, IngestOutcome.Created);

        // 1) identical room/dates/price -> duplicate; details are never rewritten. A strictly newer timestamp advances
        //    only the freshness marker, so a delayed older update can't later overwrite state the supplier re-confirmed.
        //    An equal or older timestamp writes nothing and never moves the marker backwards.
        if (existing.HasSameDetailsAs(incoming))
        {
            return incoming.UpdatedAtUtc > existing.UpdatedAtUtc
                ? new(ReservationAction.TouchTimestamp, IngestOutcome.Unchanged)
                : new(ReservationAction.None, IngestOutcome.Unchanged);
        }

        // 2) different details but older than what we hold -> stale, ignore
        if (incoming.UpdatedAtUtc < existing.UpdatedAtUtc)
            return new(ReservationAction.None, IngestOutcome.Stale);

        // 3) different details, same-or-newer -> update (an equal timestamp is applied)
        return new(ReservationAction.Update, IngestOutcome.Updated);
    }
}
