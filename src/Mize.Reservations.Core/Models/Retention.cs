namespace Mize.Reservations.Core.Models;

/// <summary>
/// Retention rules. Disabled by default. When enabled, a reservation whose <c>checkOut</c> is older than
/// <see cref="RetentionDays"/> is eligible for archiving and purging, and ingest ignores updates for such stays
/// (the resurrection guard: without it, a late retry for a purged reservation would be accepted as a brand-new one).
/// </summary>
public sealed record RetentionPolicy(
    bool Enabled = false,
    int RetentionDays = 365,
    int BatchSize = 1000,
    int MaxBatchesPerRun = 100,
    bool DryRun = false)
{
    public static RetentionPolicy Disabled { get; } = new();

    /// <summary>Stays that ended strictly before this instant are expired.</summary>
    public DateTime CutoffUtc(DateTime nowUtc) => nowUtc.AddDays(-RetentionDays);

    public bool IsExpired(Reservation reservation, DateTime nowUtc) =>
        Enabled && reservation.CheckOutUtc < CutoffUtc(nowUtc);

    /// <summary>Builds the policy from a key lookup (<c>Retention:*</c> keys), so Core needs no configuration package.</summary>
    public static RetentionPolicy From(Func<string, string?> get)
    {
        bool Bool(string k, bool d) => bool.TryParse(get(k), out var v) ? v : d;
        int Int(string k, int d) => int.TryParse(get(k), out var v) && v > 0 ? v : d;

        var defaults = new RetentionPolicy();
        return new RetentionPolicy(
            Enabled: Bool("Retention:Enabled", defaults.Enabled),
            RetentionDays: Int("Retention:RetentionDays", defaults.RetentionDays),
            BatchSize: Int("Retention:BatchSize", defaults.BatchSize),
            MaxBatchesPerRun: Int("Retention:MaxBatchesPerRun", defaults.MaxBatchesPerRun),
            DryRun: Bool("Retention:DryRun", defaults.DryRun));
    }
}

/// <summary>One archive object: its key (path) and the reservations it holds.</summary>
public sealed record ArchiveBatch(string Key, IReadOnlyList<Reservation> Reservations);

public sealed record RetentionRunResult(
    DateTime CutoffUtc,
    bool DryRun,
    int Batches,
    int Archived,
    int ArchiveObjects,
    int Deleted,
    int SkippedBecauseChanged);
