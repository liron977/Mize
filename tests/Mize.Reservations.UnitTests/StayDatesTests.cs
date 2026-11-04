using FluentAssertions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>checkOut must be strictly after checkIn, compared as instants (so the offset a timestamp is written in never matters).</summary>
public class StayDatesTests
{
    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private static IngestCommand Cmd(DateTimeOffset? checkIn, DateTimeOffset? checkOut, decimal? price = 450m,
        IReadOnlyDictionary<string, string[]>? formatErrors = null) =>
        new("acme", "r1", "room1", checkIn, checkOut, price, At("2026-07-01T10:15:00Z"), formatErrors);

    private static Dictionary<string, string[]> Errors(IngestCommand cmd) => IngestValidator.Validate(cmd, "acme").Errors;

    [Theory]
    [InlineData("2026-08-01T00:00:00Z", "2026-08-03T00:00:00Z", true)]               // a normal stay
    [InlineData("2026-08-01T00:00:00Z", "2026-08-01T00:00:01Z", true)]               // one second later is the smallest valid stay
    [InlineData("2026-08-01T14:00:00Z", "2026-08-02T11:00:00Z", true)]               // typical hotel stay: in at 14:00, out next day 11:00
    [InlineData("2026-08-01T22:00:00Z", "2026-08-01T20:00:00-05:00", true)]          // 20:00-05:00 is 01:00Z the next day: later by instant even though its clock text (20:00) looks earlier than 22:00
    [InlineData("2026-08-01T00:00:00Z", "2026-08-01T00:00:00Z", false)]              // equal is not after
    [InlineData("2026-08-01T00:00:01Z", "2026-08-01T00:00:00Z", false)]              // one second before
    [InlineData("2026-08-03T00:00:00Z", "2026-08-01T00:00:00Z", false)]              // two days before
    [InlineData("2026-08-01T02:00:00+02:00", "2026-08-01T00:00:00Z", false)]         // the SAME instant written in two offsets
    [InlineData("2026-08-01T05:00:00Z", "2026-08-01T09:00:00+10:00", false)]         // later clock text (09:00) but EARLIER instant (23:00Z the day before)
    [InlineData("2026-08-01T00:00:00+14:00", "2026-07-31T10:00:00Z", false)]         // extreme offset: checkIn is 2026-07-31T10:00Z, equal
    public void Only_a_checkOut_strictly_later_by_instant_is_accepted(string checkIn, string checkOut, bool valid)
    {
        var errors = Errors(Cmd(At(checkIn), At(checkOut)));

        if (valid) errors.Should().BeEmpty();
        else errors.Keys.Should().BeEquivalentTo(new[] { "checkOut" }, "the error belongs to checkOut");
    }

    [Fact]
    public void The_error_names_both_values_so_the_caller_can_see_what_clashed()
    {
        var message = Errors(Cmd(At("2026-08-03T00:00:00Z"), At("2026-08-01T00:00:00Z")))["checkOut"].Single();

        message.Should().Contain("must be after checkIn")
            .And.Contain("checkIn 2026-08-03T00:00:00Z")
            .And.Contain("checkOut 2026-08-01T00:00:00Z")
            .And.Contain("UTC");
    }

    [Fact]
    public void An_offset_is_normalised_in_the_message_so_it_is_always_comparable()
    {
        var message = Errors(Cmd(At("2026-08-01T02:00:00+02:00"), At("2026-08-01T00:00:00Z")))["checkOut"].Single();

        message.Should().Contain("checkIn 2026-08-01T00:00:00Z").And.Contain("checkOut 2026-08-01T00:00:00Z");
    }

    [Fact]
    public void A_missing_date_is_reported_as_required_and_never_compared()
    {
        var onlyOut = Errors(Cmd(null, At("2026-08-03T00:00:00Z")));
        var onlyIn = Errors(Cmd(At("2026-08-01T00:00:00Z"), null));

        onlyOut.Keys.Should().BeEquivalentTo("checkIn");
        onlyOut["checkIn"].Single().Should().Contain("required");
        onlyIn.Keys.Should().BeEquivalentTo("checkOut");
        onlyIn["checkOut"].Single().Should().Contain("required");
    }

    [Fact]
    public void A_checkOut_that_has_its_own_problem_is_not_also_reported_as_before_checkIn()
    {
        // checkOut is in an implausible year: that is the one thing wrong with it; do not pile a second message on top
        var errors = Errors(Cmd(At("2026-08-01T00:00:00Z"), At("1850-01-01T00:00:00Z")));

        errors["checkOut"].Should().ContainSingle().Which.Should().Contain("between the years");
    }

    [Fact]
    public void A_checkOut_the_adapter_could_not_read_is_not_compared_and_keeps_its_format_error()
    {
        var format = new Dictionary<string, string[]> { ["checkOut"] = ["checkOut must be a JSON string in ISO 8601 ..."] };

        var errors = Errors(Cmd(At("2026-08-01T00:00:00Z"), null, formatErrors: format));

        errors.Should().NotContainKey("checkOut"); // the adapter's explanation is the one the caller gets (merged by the service)
    }

    [Fact]
    public void Date_problems_are_reported_together_with_other_problems()
    {
        var errors = Errors(Cmd(At("2026-08-03T00:00:00Z"), At("2026-08-01T00:00:00Z"), price: -5m));

        errors.Keys.Should().BeEquivalentTo("checkOut", "price");
    }

    [Fact]
    public async Task A_reservation_with_checkOut_before_checkIn_is_never_stored()
    {
        var repo = new FakeReservations();
        var svc = new IngestService(new FakeRateLimiter(100), repo, new FakeStats());

        var result = await svc.IngestAsync(Cmd(At("2026-08-03T00:00:00Z"), At("2026-08-01T00:00:00Z")));

        result.Outcome.Should().Be(IngestOutcome.Invalid);
        result.Errors!.Should().ContainKey("checkOut");
        repo.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task The_stored_utc_values_keep_the_instants_the_supplier_meant()
    {
        var repo = new FakeReservations();
        var svc = new IngestService(new FakeRateLimiter(100), repo, new FakeStats());

        await svc.IngestAsync(Cmd(At("2026-08-01T02:00:00+02:00"), At("2026-08-03T05:30:00+05:30")));

        var stored = repo.Received.Single();
        stored.CheckInUtc.Should().Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        stored.CheckOutUtc.Should().Be(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));
    }
}
