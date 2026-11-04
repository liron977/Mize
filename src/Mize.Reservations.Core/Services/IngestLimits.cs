namespace Mize.Reservations.Core.Services;

/// <summary>Size and range limits for ingest input. Generous for real data, small enough that nothing unbounded can be stored.</summary>
public static class IngestLimits
{
    public const int MaxSupplierIdLength = 100;
    public const int MaxReservationIdLength = 200;
    public const int MaxRoomIdLength = 100;

    /// <summary>Timestamps outside these years are certainly typos or garbage (year 0001, 9999, epoch mistakes).</summary>
    public const int MinYear = 1900;
    public const int MaxYear = 2200;

    /// <summary>Maximum request body. A real payload is a few hundred bytes.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    /// <summary>Null when the (already trimmed) text is acceptable for an identifier; otherwise what is wrong with it.</summary>
    public static string? IdentifierProblem(string value, int maxLength)
    {
        if (value.Length > maxLength) return $"must be at most {maxLength} characters.";
        if (value.Any(char.IsControl)) return "must not contain control characters.";
        return null;
    }
}
