using Dapper;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Storage.Sqlite;

public sealed class StatsRepository(SqliteConnectionFactory factory, WriteBatcher batcher) : IStatsRepository
{
    private sealed class StatsRow
    {
        public long Ingested { get; set; }
        public long Throttled { get; set; }
        public long Unchanged { get; set; }
        public long Stale { get; set; }
    }

    // Goes through the group-commit writer, so a burst of 429s coalesces into one counter write per batch.
    public Task IncrementThrottledAsync(string supplierId, CancellationToken ct = default) =>
        batcher.EnqueueThrottled(supplierId);

    // Reads don't block on the writer (WAL).
    public async Task<SupplierStats> GetAsync(string supplierId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<StatsRow>(
            "SELECT Ingested, Throttled, Unchanged, Stale FROM SupplierStats WHERE SupplierId = @supplierId",
            new { supplierId });
        return row is null
            ? new SupplierStats(supplierId, 0, 0, 0, 0)
            : new SupplierStats(supplierId, row.Ingested, row.Throttled, row.Unchanged, row.Stale);
    }
}
