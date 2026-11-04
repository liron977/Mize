using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Api.Contracts;

/// <summary>
/// Reads the ingest payload STRICTLY and explains every problem, instead of guessing:
///   - the body must be a JSON object; property names are matched case-insensitively and unknown properties are ignored;
///   - supplierId, reservationId, roomId must be JSON strings (a number or boolean is rejected, not converted);
///   - checkIn, checkOut, updatedAtUtc must be JSON strings in ISO 8601 date-time form WITH an explicit offset
///     ("2026-08-01T00:00:00Z" or "...+02:00"). A timestamp without an offset or a date without a time is rejected: it would
///     silently be read in the server's local time, which is wrong data that looks right;
///   - price must be a JSON number (the string "450" is rejected, not coerced).
/// A field that cannot be read becomes null in the command and its explanation goes into <see cref="IngestCommand.FormatErrors"/>.
/// Business rules (required, lengths, ranges, checkOut after checkIn, price rules) live in Core.
/// </summary>
public static class IngestRequestParser
{
    public const string TimestampHint =
        "must be a JSON string in ISO 8601 date-time format with an explicit UTC offset, for example \"2026-08-01T00:00:00Z\" or \"2026-08-01T02:00:00+02:00\".";

    private static readonly string[] TimestampFormats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];
    private static readonly Regex EndsWithOffset = new(@"(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IngestCommand Parse(JsonElement root)
    {
        var errors = new Dictionary<string, string[]>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            errors["body"] = ["The request body must be a JSON object."];
            return new IngestCommand(null, null, null, null, null, null, null, errors);
        }

        var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in root.EnumerateObject()) props[p.Name] = p.Value; // the last duplicate wins

        // missing and null are the same ("required" is reported by the business rules); any other wrong type is reported here
        string? Text(string name)
        {
            if (!props.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            errors[name] = [$"{name} must be a JSON string, not a {Describe(v.ValueKind)}."];
            return null;
        }

        DateTimeOffset? Timestamp(string name)
        {
            if (!props.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String
                && v.GetString() is { } s
                && EndsWithOffset.IsMatch(s)
                && DateTimeOffset.TryParseExact(s, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;

            errors[name] = [$"{name} {TimestampHint}"];
            return null;
        }

        decimal? Price()
        {
            if (!props.TryGetValue("price", out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.Number)
            {
                if (v.TryGetDecimal(out var d)) return d;
                errors["price"] = ["price is not a valid decimal number (it is too large or not representable)."];
                return null;
            }
            errors["price"] = [$"price must be a JSON number such as 450.00, not a {Describe(v.ValueKind)}."];
            return null;
        }

        var supplierId = Text("supplierId");
        var reservationId = Text("reservationId");
        var roomId = Text("roomId");
        var checkIn = Timestamp("checkIn");
        var checkOut = Timestamp("checkOut");
        var price = Price();
        var updatedAtUtc = Timestamp("updatedAtUtc");

        return new IngestCommand(supplierId, reservationId, roomId, checkIn, checkOut, price, updatedAtUtc,
            errors.Count > 0 ? errors : null);
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "JSON object",
        JsonValueKind.Array => "JSON array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "value"
    };
}
