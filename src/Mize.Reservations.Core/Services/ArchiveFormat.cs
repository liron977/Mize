using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Core.Services;

/// <summary>
/// The archive object format, shared by every archive adapter: gzipped JSON Lines, one reservation per line, versioned
/// (<c>v</c>) and self-describing so it can be read by other tools (for example Athena or DuckDB) without this code.
/// </summary>
public static class ArchiveFormat
{
    public const int Version = 1;

    private sealed record Line(int V, string SupplierId, string ReservationId, string RoomId,
        string CheckIn, string CheckOut, long PriceCents, string UpdatedAtUtc);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(IReadOnlyList<Reservation> reservations)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            foreach (var r in reservations)
                writer.WriteLine(JsonSerializer.Serialize(new Line(Version, r.SupplierId, r.ReservationId, r.RoomId,
                    Iso(r.CheckInUtc), Iso(r.CheckOutUtc), r.PriceCents, Iso(r.UpdatedAtUtc)), Json));
        }
        return output.ToArray();
    }

    public static IReadOnlyList<Reservation> Deserialize(byte[] data)
    {
        var result = new List<Reservation>();
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (reader.ReadLine() is { Length: > 0 } text)
        {
            var l = JsonSerializer.Deserialize<Line>(text, Json) ?? throw new InvalidDataException("Empty archive record.");
            if (l.V != Version) throw new InvalidDataException($"Unsupported archive record version {l.V}.");
            result.Add(new Reservation(l.SupplierId, l.ReservationId, l.RoomId,
                Parse(l.CheckIn), Parse(l.CheckOut), l.PriceCents, Parse(l.UpdatedAtUtc)));
        }
        return result;
    }

    private static string Iso(DateTime utc) => utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime Parse(string s) =>
        // The stored value is "O" format ending in 'Z'; RoundtripKind turns that back into a UTC DateTime with full precision.
        // (It must not be combined with AdjustToUniversal: that combination is invalid and throws.)
        DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
}
