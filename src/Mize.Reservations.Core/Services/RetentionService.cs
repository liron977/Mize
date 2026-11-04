using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Core.Services;

/// <summary>
/// Archives expired reservations, then deletes them locally. Per page of expired rows:
/// 1) read the page, 2) write it to the archive and wait until it is durable, 3) delete only the rows that are still
/// exactly what was archived. A row is never deleted before its archive copy is durable, and every step is safe to
/// repeat: after a crash the same rows are simply archived again (same keys, identical content) and then deleted.
/// A row changed by a supplier in between is skipped and re-examined on the next run.
/// The archive is therefore at-least-once: a record may appear in more than one object, so readers dedupe by
/// (supplierId, reservationId, updatedAtUtc).
/// </summary>
public sealed class RetentionService(
    IReservationRetentionStore store,
    IReservationArchive archive,
    RetentionPolicy policy,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<RetentionRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        var cutoff = policy.CutoffUtc(_time.GetUtcNow().UtcDateTime);
        if (!policy.Enabled) return new RetentionRunResult(cutoff, policy.DryRun, 0, 0, 0, 0, 0);

        Reservation? cursor = null;
        int batches = 0, archived = 0, objects = 0, deleted = 0, skipped = 0;

        while (batches < policy.MaxBatchesPerRun)
        {
            var page = await store.GetExpiredAsync(cutoff, policy.BatchSize, cursor, ct);
            if (page.Count == 0) break;

            batches++;
            cursor = page[^1]; // keyset paging: never re-reads a page

            archived += page.Count;
            if (!policy.DryRun)
            {
                foreach (var batch in ArchiveLayout.Partition(page))
                {
                    await archive.WriteAsync(batch, ct);   // durable on return, or this throws and nothing is deleted
                    objects++;
                }

                var removed = await store.DeleteIfUnchangedAsync(page, ct);
                deleted += removed;
                skipped += page.Count - removed;
            }

            if (page.Count < policy.BatchSize) break;
        }

        if (!policy.DryRun && deleted > 0) await store.ReclaimSpaceAsync(ct);

        return new RetentionRunResult(cutoff, policy.DryRun, batches, archived, objects, deleted, skipped);
    }
}
