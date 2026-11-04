namespace Mize.Reservations.Core.Services;

public static class SupplierKey
{
    /// <summary>
    /// Canonical supplier key used for rate limiting, storage and stats: trimmed and
    /// case-insensitive, so "Acme" and " acme " share a single quota (no bypass by re-casing).
    /// </summary>
    public static string Normalize(string? supplierId) =>
        (supplierId ?? string.Empty).Trim().ToLowerInvariant();
}
