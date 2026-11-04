using FluentAssertions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>The dedup/upsert rules, tested once, with no storage engine anywhere.</summary>
public class ReservationDeciderTests
{
    private static DateTime T(int day) => new(2026, 7, day, 10, 0, 0, DateTimeKind.Utc);

    private static Reservation R(long price = 45000, string room = "room1", int day = 1) =>
        new("acme", "r1", room, new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc), price, T(day));

    [Fact]
    public void No_existing_row_inserts_and_is_Created() =>
        ReservationDecider.Decide(null, R()).Should().Be(new ReservationDecision(ReservationAction.Insert, IngestOutcome.Created));

    [Fact]
    public void Identical_details_with_a_newer_timestamp_only_touches_the_timestamp() =>
        ReservationDecider.Decide(R(day: 1), R(day: 5))
            .Should().Be(new ReservationDecision(ReservationAction.TouchTimestamp, IngestOutcome.Unchanged));

    [Fact]
    public void Identical_details_with_an_equal_timestamp_writes_nothing() =>
        ReservationDecider.Decide(R(day: 3), R(day: 3))
            .Should().Be(new ReservationDecision(ReservationAction.None, IngestOutcome.Unchanged));

    [Fact]
    public void Identical_details_with_an_older_timestamp_writes_nothing_and_is_Unchanged_not_Stale() =>
        ReservationDecider.Decide(R(day: 5), R(day: 1))
            .Should().Be(new ReservationDecision(ReservationAction.None, IngestOutcome.Unchanged));

    [Fact]
    public void Different_details_with_an_older_timestamp_is_Stale_and_writes_nothing() =>
        ReservationDecider.Decide(R(price: 45000, day: 5), R(price: 100, day: 1))
            .Should().Be(new ReservationDecision(ReservationAction.None, IngestOutcome.Stale));

    [Theory]
    [InlineData(2)] // newer
    [InlineData(1)] // equal: applied
    public void Different_details_with_a_same_or_newer_timestamp_updates(int incomingDay) =>
        ReservationDecider.Decide(R(price: 45000, day: 1), R(price: 100, day: incomingDay))
            .Should().Be(new ReservationDecision(ReservationAction.Update, IngestOutcome.Updated));

    [Fact]
    public void A_room_change_alone_counts_as_different_details() =>
        ReservationDecider.Decide(R(room: "a"), R(room: "b", day: 2)).Action.Should().Be(ReservationAction.Update);

    [Theory]
    [InlineData(IngestOutcome.Created, StatsCounter.Ingested)]
    [InlineData(IngestOutcome.Updated, StatsCounter.Ingested)]
    [InlineData(IngestOutcome.Unchanged, StatsCounter.Unchanged)]
    [InlineData(IngestOutcome.Stale, StatsCounter.Stale)]
    [InlineData(IngestOutcome.Throttled, StatsCounter.Throttled)]
    public void Outcomes_map_to_their_counter(IngestOutcome outcome, StatsCounter expected) =>
        outcome.ToCounter().Should().Be(expected);

    [Fact]
    public void Invalid_has_no_counter()
    {
        var act = () => IngestOutcome.Invalid.ToCounter();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
