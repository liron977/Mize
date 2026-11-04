using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Retention.Worker;

/// <summary>
/// Used only when an operator explicitly sets <c>Retention:AllowDeleteWithoutArchive=true</c>: expired rows are deleted
/// with NO archive copy. It exists so that purging without an archive is a deliberate, visible decision, never a default.
/// </summary>
public sealed class DiscardingReservationArchive : IReservationArchive
{
    public Task WriteAsync(ArchiveBatch batch, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Reservation>> ReadAsync(string key, CancellationToken ct = default) =>
        throw new KeyNotFoundException("The discarding archive keeps nothing.");

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
