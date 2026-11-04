using System.Security.Cryptography;
using System.Text;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Core.Services;

/// <summary>
/// Where archive objects go: <c>reservations/v1/checkout=YYYY-MM/supplier=&lt;encoded&gt;/&lt;batchId&gt;.jsonl.gz</c>
/// (Hive-style partitions, so query engines can prune by month and supplier). The batch id is a hash of the batch's
/// content, so archiving the same rows again produces the same key: a retry overwrites an identical object.
/// </summary>
public static class ArchiveLayout
{
    public const string Root = "reservations/v1/";

    /// <summary>Splits rows into one batch per (check-out month, supplier).</summary>
    public static IReadOnlyList<ArchiveBatch> Partition(IEnumerable<Reservation> reservations) =>
        reservations
            .GroupBy(r => (Month: r.CheckOutUtc.ToString("yyyy-MM"), r.SupplierId))
            .OrderBy(g => g.Key.Month, StringComparer.Ordinal).ThenBy(g => g.Key.SupplierId, StringComparer.Ordinal)
            .Select(g =>
            {
                var rows = g.OrderBy(r => r.ReservationId, StringComparer.Ordinal).ToList();
                var key = $"{Root}checkout={g.Key.Month}/supplier={EncodeSegment(g.Key.SupplierId)}/{BatchId(rows)}.jsonl.gz";
                return new ArchiveBatch(key, rows);
            })
            .ToList();

    /// <summary>
    /// Every archive adapter calls this before touching storage: a key must live under <see cref="Root"/> and must not contain
    /// traversal segments, backslashes or control characters. (A file system could escape its root; S3 has no traversal,
    /// but one rule keeps every adapter, and the keys other tools see, consistent.)
    /// </summary>
    public static void EnsureValidKey(string key)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith(Root, StringComparison.Ordinal)
            || key.Contains('\\') || key.Any(char.IsControl)
            || key.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException($"Invalid archive key '{key}': it must live under '{Root}' with no traversal.", nameof(key));
    }

    /// <summary>Same safety rules for a listing prefix (which may be empty or partial).</summary>
    public static void EnsureValidPrefix(string prefix)
    {
        if (prefix.Contains('\\') || prefix.Any(char.IsControl) || prefix.Split('/').Any(s => s is "." or ".."))
            throw new ArgumentException($"Invalid archive prefix '{prefix}'.", nameof(prefix));
    }

    public static string BatchId(IEnumerable<Reservation> sortedRows)
    {
        var text = string.Join("\n", sortedRows.Select(r =>
            $"{r.SupplierId}|{r.ReservationId}|{r.UpdatedAtUtc:O}|{r.RoomId}|{r.PriceCents}|{r.CheckInUtc:O}|{r.CheckOutUtc:O}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Makes a supplier id safe as a single path/key segment: only [a-z0-9_-] pass through; everything else (including
    /// '.', '/', ':' and upper-case letters) becomes %XX per UTF-8 byte. That blocks path traversal ("..") and
    /// collisions on case-insensitive file systems.
    /// </summary>
    public static string EncodeSegment(string value)
    {
        if (value.Length == 0) return "_empty";
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var ch = (char)b;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-') sb.Append(ch);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
