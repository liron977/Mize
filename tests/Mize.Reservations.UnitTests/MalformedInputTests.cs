using System.Text.Json;
using FluentAssertions;
using Mize.Reservations.Api.Contracts;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>Reading the payload strictly: every wrong shape is reported with an explanation, nothing is guessed or coerced.</summary>
public class IngestRequestParserTests
{
    private const string Good =
        "\"supplierId\":\"acme\",\"reservationId\":\"r1\",\"roomId\":\"room1\",\"checkIn\":\"2026-08-01T00:00:00Z\"," +
        "\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":450.00,\"updatedAtUtc\":\"2026-07-01T10:15:00Z\"";

    private static IngestCommand Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return IngestRequestParser.Parse(doc.RootElement);
    }

    /// <summary>The good payload with ONE field replaced by raw JSON.</summary>
    private static string With(string field, string rawJsonValue)
    {
        var fields = Good.Split(",\"").Select((p, i) => i == 0 ? p : "\"" + p).ToList();
        var index = fields.FindIndex(f => f.StartsWith($"\"{field}\":"));
        fields[index] = $"\"{field}\":{rawJsonValue}";
        return "{" + string.Join(",", fields) + "}";
    }

    [Fact]
    public void The_spec_example_parses_to_a_typed_command_with_no_errors()
    {
        var cmd = Parse("{" + Good + "}");

        cmd.FormatErrors.Should().BeNull();
        (cmd.SupplierId, cmd.ReservationId, cmd.RoomId).Should().Be(("acme", "r1", "room1"));
        cmd.Price.Should().Be(450.00m);
        cmd.CheckIn!.Value.UtcDateTime.Should().Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        cmd.UpdatedAtUtc!.Value.UtcDateTime.Should().Be(new DateTime(2026, 7, 1, 10, 15, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void An_offset_is_honoured_so_the_same_instant_can_be_sent_in_any_zone()
    {
        var cmd = Parse(With("checkIn", "\"2026-08-01T02:00:00+02:00\""));

        cmd.FormatErrors.Should().BeNull();
        cmd.CheckIn!.Value.UtcDateTime.Should().Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("2026-08-01T00:00:00Z")]
    [InlineData("2026-08-01T00:00:00.5Z")]
    [InlineData("2026-08-01T00:00:00.1234567Z")]
    [InlineData("2026-08-01T00:00:00+00:00")]
    [InlineData("2026-08-01T05:30:00-05:30")]
    public void Valid_iso_8601_timestamps_with_an_offset_are_accepted(string value) =>
        Parse(With("checkIn", $"\"{value}\"")).FormatErrors.Should().BeNull();

    [Fact]
    public void Property_names_are_case_insensitive_and_unknown_properties_are_ignored()
    {
        var cmd = Parse("{\"SUPPLIERID\":\"acme\",\"ReservationId\":\"r1\",\"roomid\":\"x\",\"checkIn\":\"2026-08-01T00:00:00Z\"," +
                        "\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":1,\"updatedAtUtc\":\"2026-07-01T10:15:00Z\",\"somethingNew\":{\"a\":[1,2]}}");

        cmd.FormatErrors.Should().BeNull();
        cmd.SupplierId.Should().Be("acme");
    }

    [Fact]
    public void Missing_and_null_fields_are_not_format_errors_they_are_reported_as_required_by_the_business_rules()
    {
        var cmd = Parse("{\"supplierId\":\"acme\",\"price\":null}");

        cmd.FormatErrors.Should().BeNull();
        (cmd.ReservationId, cmd.RoomId, cmd.CheckIn, cmd.Price).Should().Be((null, null, null, null));
    }

    [Theory]
    [InlineData("supplierId", "123", "number")]
    [InlineData("supplierId", "true", "boolean")]
    [InlineData("reservationId", "{}", "JSON object")]
    [InlineData("roomId", "[\"a\"]", "JSON array")]
    public void A_string_field_with_the_wrong_json_type_is_rejected_not_converted(string field, string raw, string typeName)
    {
        var cmd = Parse(With(field, raw));

        cmd.FormatErrors.Should().ContainKey(field);
        cmd.FormatErrors![field].Single().Should().Contain("must be a JSON string").And.Contain(typeName);
    }

    [Theory]
    [InlineData("1785000000")]                         // epoch number
    [InlineData("true")]
    [InlineData("\"2026-08-01\"")]                     // date only: no time, no zone
    [InlineData("\"2026-08-01T00:00:00\"")]            // no offset: would silently be read in the server's local time
    [InlineData("\"2026-08-01 00:00:00Z\"")]           // space instead of T
    [InlineData("\"08/01/2026 00:00:00Z\"")]           // not ISO 8601
    [InlineData("\"2026-13-01T00:00:00Z\"")]           // month 13
    [InlineData("\"2026-02-30T00:00:00Z\"")]           // no such day
    [InlineData("\"2026-08-01T25:00:00Z\"")]           // hour 25
    [InlineData("\" 2026-08-01T00:00:00Z\"")]          // leading space
    [InlineData("\"2026-08-01T00:00:00Z \"")]          // trailing space
    [InlineData("\"2026-08-01T00:00:00z\"")]           // ISO 8601 uses an uppercase Z
    [InlineData("\"not a date\"")]
    [InlineData("\"\"")]
    public void A_timestamp_that_is_not_iso_8601_with_an_explicit_offset_is_rejected_with_an_example(string raw)
    {
        foreach (var field in new[] { "checkIn", "checkOut", "updatedAtUtc" })
        {
            var cmd = Parse(With(field, raw));

            cmd.FormatErrors.Should().ContainKey(field, $"{raw} must be rejected for {field}");
            cmd.FormatErrors![field].Single().Should().Contain("ISO 8601").And.Contain("2026-08-01T00:00:00Z");
        }
    }

    [Theory]
    [InlineData("\"450\"", "string")]
    [InlineData("true", "boolean")]
    [InlineData("[450]", "JSON array")]
    [InlineData("{\"amount\":450}", "JSON object")]
    public void A_price_that_is_not_a_json_number_is_rejected_not_coerced(string raw, string typeName)
    {
        var cmd = Parse(With("price", raw));

        cmd.Price.Should().BeNull();
        cmd.FormatErrors!["price"].Single().Should().Contain("must be a JSON number").And.Contain(typeName);
    }

    [Fact]
    public void A_number_too_large_for_a_decimal_is_rejected_with_an_explanation()
    {
        var cmd = Parse(With("price", "1e400"));

        cmd.FormatErrors!["price"].Single().Should().Contain("not a valid decimal number");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"hello\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void A_body_that_is_not_a_json_object_is_rejected(string json)
    {
        var cmd = Parse(json);

        cmd.FormatErrors.Should().ContainKey("body");
        cmd.FormatErrors!["body"].Single().Should().Contain("must be a JSON object");
    }

    [Fact]
    public void Every_bad_field_is_reported_at_once_not_one_per_attempt()
    {
        var cmd = Parse("{\"supplierId\":1,\"reservationId\":true,\"roomId\":\"ok\",\"checkIn\":\"2026-08-01\",\"checkOut\":5,\"price\":\"x\",\"updatedAtUtc\":null}");

        cmd.FormatErrors!.Keys.Should().BeEquivalentTo("supplierId", "reservationId", "checkIn", "checkOut", "price");
    }
}

/// <summary>The business-side limits and how format errors reach the caller.</summary>
public class IngestInputRulesTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static IngestCommand Cmd(
        string? supplier = "acme", string? reservation = "r1", string? room = "room1",
        DateTimeOffset? checkIn = null, DateTimeOffset? checkOut = null, decimal? price = 450m,
        IReadOnlyDictionary<string, string[]>? formatErrors = null, bool useDefaultDates = true) =>
        new(supplier, reservation, room,
            useDefaultDates ? new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero) : checkIn,
            useDefaultDates ? new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero) : checkOut,
            price, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), formatErrors);

    private static (IngestService svc, FakeRateLimiter limiter, FakeReservations repo) Build()
    {
        var limiter = new FakeRateLimiter(100);
        var repo = new FakeReservations();
        return (new IngestService(limiter, repo, new FakeStats(), null, new FixedUtcTimeProvider(Now)), limiter, repo);
    }

    // ---- limits in the business rules

    [Fact]
    public void Identifiers_longer_than_their_limits_are_rejected()
    {
        var (_, errors) = IngestValidator.Validate(Cmd(reservation: new string('r', IngestLimits.MaxReservationIdLength + 1),
            room: new string('x', IngestLimits.MaxRoomIdLength + 1)), "acme");

        errors["reservationId"].Single().Should().Contain("at most 200 characters");
        errors["roomId"].Single().Should().Contain("at most 100 characters");
    }

    [Fact]
    public void Identifiers_exactly_at_their_limits_are_accepted()
    {
        var (reservation, errors) = IngestValidator.Validate(Cmd(reservation: new string('r', IngestLimits.MaxReservationIdLength),
            room: new string('x', IngestLimits.MaxRoomIdLength)), "acme");

        errors.Should().BeEmpty();
        reservation.Should().NotBeNull();
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("tab\there")]
    [InlineData("nul\0byte")]
    [InlineData("esc\u001b[31m")]
    public void Control_characters_in_identifiers_are_rejected(string value)
    {
        var (_, errors) = IngestValidator.Validate(Cmd(reservation: value, room: value), "acme");

        errors["reservationId"].Single().Should().Contain("control characters");
        errors["roomId"].Single().Should().Contain("control characters");
    }

    [Theory]
    [InlineData(1899, false)]
    [InlineData(1900, true)]
    [InlineData(2200, true)]
    [InlineData(2201, false)]
    public void Timestamps_outside_the_plausible_years_are_rejected(int year, bool ok)
    {
        var at = new DateTimeOffset(year, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var (_, errors) = IngestValidator.Validate(Cmd(checkIn: at, checkOut: at.AddDays(1), useDefaultDates: false), "acme");

        if (ok) errors.Should().NotContainKey("checkIn");
        else errors["checkIn"].Single().Should().Contain("between the years 1900 and 2200");
    }

    // ---- format errors from the adapter

    [Fact]
    public async Task A_field_the_adapter_could_not_read_reports_its_own_explanation_not_a_misleading_required()
    {
        var (svc, _, repo) = Build();
        var format = new Dictionary<string, string[]> { ["checkIn"] = ["checkIn must be a JSON string in ISO 8601 ..."] };

        var result = await svc.IngestAsync(Cmd(formatErrors: format, useDefaultDates: false, checkOut: new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero)));

        result.Outcome.Should().Be(IngestOutcome.Invalid);
        result.Errors!["checkIn"].Single().Should().Contain("ISO 8601").And.NotContain("required");
        repo.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Format_errors_and_business_errors_are_reported_together()
    {
        var (svc, _, _) = Build();
        var format = new Dictionary<string, string[]> { ["price"] = ["price must be a JSON number ..."] };

        var result = await svc.IngestAsync(Cmd(room: "", price: null, formatErrors: format));

        result.Errors!.Keys.Should().BeEquivalentTo("price", "roomId");
    }

    [Fact]
    public async Task A_request_with_a_valid_supplier_but_unreadable_fields_still_costs_rate_limit_quota()
    {
        var (svc, limiter, _) = Build();
        var format = new Dictionary<string, string[]> { ["price"] = ["price must be a JSON number ..."] };

        for (var i = 0; i < 3; i++) await svc.IngestAsync(Cmd(price: null, formatErrors: format));

        limiter.Calls["acme"].Should().Be(3); // malformed traffic can be throttled like any other
    }

    [Fact]
    public async Task An_unreadable_supplier_id_reports_the_format_error_and_never_reaches_the_limiter()
    {
        var (svc, limiter, _) = Build();
        var format = new Dictionary<string, string[]> { ["supplierId"] = ["supplierId must be a JSON string, not a number."] };

        var result = await svc.IngestAsync(Cmd(supplier: null, formatErrors: format));

        result.Errors!["supplierId"].Single().Should().Contain("must be a JSON string");
        limiter.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_body_level_format_error_is_reported_and_not_replaced_by_supplier_required()
    {
        var (svc, limiter, _) = Build();
        var format = new Dictionary<string, string[]> { ["body"] = ["The request body must be a JSON object."] };

        var result = await svc.IngestAsync(new IngestCommand(null, null, null, null, null, null, null, format));

        result.Errors!.Keys.Should().BeEquivalentTo("body");   // exactly what is wrong, not a misleading "supplierId is required"
        limiter.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Other_unreadable_fields_are_reported_together_with_the_missing_supplier()
    {
        var (svc, _, _) = Build();
        var format = new Dictionary<string, string[]> { ["price"] = ["price must be a JSON number ..."] };

        var result = await svc.IngestAsync(Cmd(supplier: null, price: null, formatErrors: format));

        result.Errors!.Keys.Should().BeEquivalentTo("price", "supplierId");
    }

    [Theory]
    [InlineData(101)]
    [InlineData(5000)]
    public async Task An_oversized_supplier_id_is_rejected_before_it_can_become_a_rate_limit_key(int length)
    {
        var (svc, limiter, repo) = Build();

        var result = await svc.IngestAsync(Cmd(supplier: new string('s', length)));

        result.Outcome.Should().Be(IngestOutcome.Invalid);
        result.Errors!["supplierId"].Single().Should().Contain("at most 100 characters");
        limiter.Calls.Should().BeEmpty();   // no Redis key is ever created for it
        repo.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task A_supplier_id_with_control_characters_is_rejected_before_the_limiter()
    {
        var (svc, limiter, _) = Build();

        var result = await svc.IngestAsync(Cmd(supplier: "ac\nme"));

        result.Errors!["supplierId"].Single().Should().Contain("control characters");
        limiter.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_supplier_id_exactly_at_the_limit_is_accepted()
    {
        var (svc, _, _) = Build();
        (await svc.IngestAsync(Cmd(supplier: new string('s', IngestLimits.MaxSupplierIdLength)))).Outcome.Should().Be(IngestOutcome.Created);
    }
}
