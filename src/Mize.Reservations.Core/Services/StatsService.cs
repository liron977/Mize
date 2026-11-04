using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Core.Services;

public sealed class StatsService(IStatsRepository stats)
{
    public Task<SupplierStats> GetAsync(string supplierId, CancellationToken ct = default) =>
        stats.GetAsync(SupplierKey.Normalize(supplierId), ct);
}
