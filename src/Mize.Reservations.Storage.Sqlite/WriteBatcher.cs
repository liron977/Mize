using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Telemetry;

namespace Mize.Reservations.Storage.Sqlite;

public sealed class WriteBatcherOptions
{
    /// <summary>Max requests committed per transaction. 1 disables batching (one fsync per request).</summary>
    public int MaxBatchSize { get; set; } = 256;

    /// <summary>Bounded queue; when full, callers get <see cref="StorageBusyException"/> (HTTP 503 + Retry-After).</summary>
    public int QueueCapacity { get; set; } = 2000;
}

/// <summary>
/// Group commit. All SQLite writes go through one writer loop that drains whatever is queued and
/// commits it as ONE transaction, so a single fsync covers many requests instead of one each.
///
/// Durability: a caller's task completes only AFTER its batch has committed, so an acknowledged
/// request is always durable. Requests still queued when the process dies were never acknowledged;
/// the supplier retries and dedup makes the retry safe.
///
/// There is no artificial delay: at low load a batch holds one item (same latency as before), and
/// batches grow by themselves as load rises.
/// </summary>
public sealed class WriteBatcher : IHostedService, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly int _maxBatch;
    private readonly Channel<WorkItem> _channel;
    private readonly ILogger<WriteBatcher>? _logger;
    private readonly ReservationMetrics? _metrics;
    private Task? _loop;

    public WriteBatcher(SqliteConnectionFactory factory, WriteBatcherOptions options, ILogger<WriteBatcher>? logger = null,
        ReservationMetrics? metrics = null)
    {
        _factory = factory;
        _logger = logger;
        _metrics = metrics;
        _maxBatch = Math.Max(1, options.MaxBatchSize);
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(Math.Max(1, options.QueueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait // TryWrite returns false when full
        });
        _metrics?.RegisterQueueDepth(() => _channel.Reader.Count);
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <summary>Stops accepting work, then drains and commits everything already queued.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (_loop is not null) await _loop.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        if (_loop is not null) await _loop;
    }

    public Task<IngestOutcome> EnqueueUpsert(Reservation reservation)
    {
        var item = new UpsertItem(reservation);
        Enqueue(item);
        return item.Result;
    }

    // At most one queued throttle item per supplier: concurrent rejections JOIN the pending item instead of
    // each taking a queue slot, so a flood of 429s can't fill the queue and starve real writes. Every joiner
    // still awaits that item's commit, so nothing is acknowledged before its count is durable.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ThrottledItem> _pendingThrottles = new();

    public Task EnqueueThrottled(string supplierId)
    {
        while (true)
        {
            if (_pendingThrottles.TryGetValue(supplierId, out var existing))
            {
                if (existing.TryJoin()) return existing.Result;
                // sealed: the writer is already taking its count; drop it and start a fresh item
                _pendingThrottles.TryRemove(new KeyValuePair<string, ThrottledItem>(supplierId, existing));
                continue;
            }

            var item = new ThrottledItem(supplierId, _pendingThrottles);
            if (!_pendingThrottles.TryAdd(supplierId, item)) continue; // lost the race; re-read

            try
            {
                Enqueue(item);
            }
            catch (Exception ex)
            {
                item.Fail(ex); // seals + removes, so anyone who already joined fails with it and nothing stays stuck
                throw;
            }
            return item.Result;
        }
    }

    /// <summary>Retention: deletes each row only if still identical to the archived version; resolves to the number deleted.</summary>
    public Task<int> EnqueueDeleteIfUnchanged(IReadOnlyList<Reservation> archived)
    {
        var item = new DeleteIfUnchangedItem(archived);
        Enqueue(item);
        return item.Result;
    }

    private void Enqueue(WorkItem item)
    {
        if (!_channel.Writer.TryWrite(item))
        {
            _metrics?.RecordStorageRejection(ReservationMetrics.RejectionReason.QueueFull);
            throw new StorageBusyException("Write queue is full or shutting down.");
        }
    }

    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<WorkItem>(_maxBatch);

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < _maxBatch && reader.TryRead(out var item)) batch.Add(item);
            if (batch.Count == 0) continue;

            try
            {
                await ProcessAsync(batch).ConfigureAwait(false);
            }
            catch (Exception ex) // never let the loop die
            {
                _logger?.LogError(ex, "Write batch failed.");
                foreach (var item in batch) item.Fail(Map(ex)); // no-op for items already completed
            }
        }
    }

    private async Task ProcessAsync(List<WorkItem> batch)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var deltas = new Dictionary<(string Supplier, StatsCounter Counter), long>();
        var applied = new List<WorkItem>(batch.Count);
        var failed = new List<(WorkItem Item, Exception Error)>();

        await using (var conn = await _factory.OpenAsync().ConfigureAwait(false))
        {
            // IMMEDIATE: take the write lock once for the whole batch.
            await conn.ExecuteAsync("BEGIN IMMEDIATE").ConfigureAwait(false);
            try
            {
                foreach (var item in batch)
                {
                    // Per-item savepoint: one bad item rolls back alone, the rest of the batch commits.
                    await conn.ExecuteAsync("SAVEPOINT item").ConfigureAwait(false);
                    try
                    {
                        await item.ApplyAsync(conn, deltas).ConfigureAwait(false);
                        await conn.ExecuteAsync("RELEASE item").ConfigureAwait(false);
                        applied.Add(item);
                    }
                    catch (Exception ex)
                    {
                        await conn.ExecuteAsync("ROLLBACK TO item").ConfigureAwait(false);
                        await conn.ExecuteAsync("RELEASE item").ConfigureAwait(false);
                        failed.Add((item, ex));
                    }
                }

                // Counters are summed per supplier/column: N increments become ONE write each.
                foreach (var ((supplier, counter), n) in deltas)
                    await ReservationRepository.BumpAsync(conn, supplier, counter, n).ConfigureAwait(false);

                await conn.ExecuteAsync("COMMIT").ConfigureAwait(false);
            }
            catch
            {
                try { await conn.ExecuteAsync("ROLLBACK").ConfigureAwait(false); } catch { /* connection already broken */ }
                throw;
            }
        }

        _metrics?.RecordBatch(batch.Count, System.Diagnostics.Stopwatch.GetElapsedTime(started));

        // Only now - after the commit - acknowledge callers.
        foreach (var item in applied) item.Succeed();
        foreach (var (item, error) in failed) item.Fail(Map(error));
    }

    private Exception Map(Exception ex)
    {
        if (ex is not SqliteException { SqliteErrorCode: 5 or 6 }) return ex;
        _metrics?.RecordStorageRejection(ReservationMetrics.RejectionReason.Busy);
        return new StorageBusyException("Database is busy.", ex);
    }

    // ---- work items -------------------------------------------------------------------------

    internal abstract class WorkItem
    {
        /// <summary>Applies the change on the batch connection and records counter deltas as its LAST step.</summary>
        public abstract Task ApplyAsync(SqliteConnection conn, Dictionary<(string, StatsCounter), long> deltas);
        public abstract void Succeed();
        public abstract void Fail(Exception ex);

        protected static void Add(Dictionary<(string, StatsCounter), long> deltas, string supplier, StatsCounter counter, long n = 1) =>
            deltas[(supplier, counter)] = deltas.GetValueOrDefault((supplier, counter)) + n;
    }

    private sealed class UpsertItem(Reservation reservation) : WorkItem
    {
        private readonly TaskCompletionSource<IngestOutcome> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IngestOutcome _outcome;

        public Task<IngestOutcome> Result => _tcs.Task;

        public override async Task ApplyAsync(SqliteConnection conn, Dictionary<(string, StatsCounter), long> deltas)
        {
            _outcome = await ReservationRepository.ApplyAsync(conn, reservation).ConfigureAwait(false);
            Add(deltas, reservation.SupplierId, _outcome.ToCounter());
        }

        public override void Succeed() => _tcs.TrySetResult(_outcome);
        public override void Fail(Exception ex) => _tcs.TrySetException(ex);
    }

    private sealed class DeleteIfUnchangedItem(IReadOnlyList<Reservation> rows) : WorkItem
    {
        private readonly TaskCompletionSource<int> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deleted;

        public Task<int> Result => _tcs.Task;

        public override async Task ApplyAsync(SqliteConnection conn, Dictionary<(string, StatsCounter), long> deltas)
        {
            _deleted = 0;
            foreach (var row in rows)
                _deleted += await ReservationRepository.DeleteIfUnchangedAsync(conn, row).ConfigureAwait(false);
            // no counter deltas: SupplierStats are lifetime totals, not row counts
        }

        public override void Succeed() => _tcs.TrySetResult(_deleted);
        public override void Fail(Exception ex) => _tcs.TrySetException(ex);
    }

    /// <summary>One queue slot standing for N rejected requests of a supplier.</summary>
    private sealed class ThrottledItem(
        string supplierId,
        System.Collections.Concurrent.ConcurrentDictionary<string, ThrottledItem> pending) : WorkItem
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private long _count = 1; // the creator's own request
        private bool _sealed;

        public Task Result => _tcs.Task;

        /// <summary>Adds one more rejected request to this item, unless the writer already took its count.</summary>
        public bool TryJoin()
        {
            lock (_gate)
            {
                if (_sealed) return false;
                _count++;
                return true;
            }
        }

        /// <summary>Freezes the count (no more joiners) and unregisters, so later rejections open a new item.</summary>
        private long Seal()
        {
            long n;
            lock (_gate) { _sealed = true; n = _count; }
            pending.TryRemove(new KeyValuePair<string, ThrottledItem>(supplierId, this));
            return n;
        }

        public override Task ApplyAsync(SqliteConnection conn, Dictionary<(string, StatsCounter), long> deltas)
        {
            Add(deltas, supplierId, StatsCounter.Throttled, Seal());
            return Task.CompletedTask;
        }

        public override void Succeed() => _tcs.TrySetResult();

        public override void Fail(Exception ex)
        {
            Seal(); // never leave a faulted item joinable
            _tcs.TrySetException(ex);
        }
    }
}
