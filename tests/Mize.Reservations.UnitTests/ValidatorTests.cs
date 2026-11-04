using FluentAssertions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Xunit;

namespace Mize.Reservations.UnitTests;

public class IngestValidatorTests
{
    private static IngestCommand Valid() => new("acme", "r1", "room1",
        DateTimeOffset.Parse("2026-08-01T00:00:00Z"), DateTimeOffset.Parse("2026-08-03T00:00:00Z"),
        450.00m, DateTimeOffset.Parse("2026-07-01T10:15:00Z"));

    [Fact]
    public void Valid_command_maps_to_reservation_with_cents()
    {
        var (r, errors) = IngestValidator.Validate(Valid(), "acme");
        errors.Should().BeEmpty();
        r!.PriceCents.Should().Be(45000);
        r.CheckInUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Offsets_are_normalised_to_utc()
    {
        var cmd = Valid() with { CheckIn = DateTimeOffset.Parse("2026-08-01T02:00:00+02:00") };
        var (r, _) = IngestValidator.Validate(cmd, "acme");
        r!.CheckInUtc.Should().Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("reservationId")]
    [InlineData("roomId")]
    public void Blank_ids_are_rejected(string field)
    {
        var cmd = field == "reservationId" ? Valid() with { ReservationId = "  " } : Valid() with { RoomId = "" };
        IngestValidator.Validate(cmd, "acme").Errors.Should().ContainKey(field);
    }

    [Fact]
    public void CheckOut_must_be_after_CheckIn()
    {
        var cmd = Valid() with { CheckOut = Valid().CheckIn };
        IngestValidator.Validate(cmd, "acme").Errors.Should().ContainKey("checkOut");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("10.005")]
    public void Bad_prices_are_rejected(string price)
    {
        var cmd = Valid() with { Price = decimal.Parse(price) };
        IngestValidator.Validate(cmd, "acme").Errors.Should().ContainKey("price");
    }

    [Fact]
    public void Missing_fields_are_all_reported()
    {
        var (r, errors) = IngestValidator.Validate(new IngestCommand("acme", null, null, null, null, null, null), "acme");
        r.Should().BeNull();
        errors.Keys.Should().BeEquivalentTo("reservationId", "roomId", "checkIn", "checkOut", "price", "updatedAtUtc");
    }
}

public class SupplierKeyTests
{
    [Theory]
    [InlineData("Acme", "acme")]
    [InlineData("  acme  ", "acme")]
    [InlineData("ACME", "acme")]
    [InlineData(null, "")]
    public void Normalize_trims_and_lowercases(string? input, string expected) =>
        SupplierKey.Normalize(input).Should().Be(expected);

    [Fact]
    public void Similar_ids_stay_distinct() =>
        SupplierKey.Normalize("acme").Should().NotBe(SupplierKey.Normalize("acme2"));
}
