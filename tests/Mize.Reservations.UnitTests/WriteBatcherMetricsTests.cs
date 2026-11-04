using FluentAssertions;
using Mize.Reservations.Contracts;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Telemetry;
using Mize.Reservations.Storage.Sqlite;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>The group-commit writer's metrics, on real SQLite (no Docker).</summary>
public sealed class WriteBatcherMetricsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mize-wbm-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly MeterProbe _probe = new();
    private readonly List<WriteBatcher> _batchers = new();

    public WriteBatcherMetricsTests()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_dir, "t.db"));
        DbInitializer.InitializeAsync(_factory).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var b in _batchers) b.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _probe.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private WriteBatcher NewBatcher(int maxBatch = 256, int capacity = 2000, bool start = true)
    {
        var b = new WriteBatcher(_factory, new WriteBatcherOptions { MaxBatchSize = maxBatch, QueueCapacity = capacity },
            metrics: _probe.NewMetrics());
        if (start) b.Start();
        _batchers.Add(b);
        return b;
    }

    private static Reservation R(string id) =>
        new("acme", id, "room1", new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc), 100,
            new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Every_committed_write_is_accounted_for_in_the_batch_size_histogram_and_each_batch_is_timed()
    {
        var repo = new ReservationRepository(_factory, NewBatcher());

        foreach (var chunk in Enumerable.Range(0, 600).Chunk(200))
            await Task.WhenAll(chunk.Select(i => repo.UpsertAsync(R($"r{i}"))));

        var sizes = _probe.Values(ReservationMetrics.BatchSizeName);
        sizes.Sum().Should().Be(600);                                   // no write unaccounted for, none counted twice
        sizes.Should().OnlyContain(s => s >= 1 && s <= 256);            // never above MaxBatchSize
        _probe.Count(ReservationMetrics.BatchCommitDurationName).Should().Be(sizes.Count); // one timing per batch
        _probe.Values(ReservationMetrics.BatchCommitDurationName).Should().OnlyContain(d => d > 0);
    }

    [Fact]
    public async Task With_batching_off_every_batch_holds_exactly_one_write()
    {
        var repo = new ReservationRepository(_factory, NewBatcher(maxBatch: 1));

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => repo.UpsertAsync(R($"r{i}"))));

        _probe.Count(ReservationMetrics.BatchSizeName).Should().Be(40);
        _probe.Values(ReservationMetrics.BatchSizeName).Should().OnlyContain(s => s == 1);
    }

    [Fact]
    public async Task Throttle_counter_writes_are_also_accounted_for_as_batch_items()
    {
        var batcher = NewBatcher();
        var stats = new StatsRepository(_factory, batcher);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => stats.IncrementThrottledAsync("noisy"))));

        // 100 rejections coalesce into very few queue items, each of which is one item in a batch
        _probe.Values(ReservationMetrics.BatchSizeName).Sum().Should().BeLessThanOrEqualTo(100).And.BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void A_full_queue_is_counted_as_a_queue_full_rejection()
    {
        var repo = new ReservationRepository(_factory, NewBatcher(capacity: 2, start: false)); // nothing drains the queue

        _ = repo.UpsertAsync(R("1"));
        _ = repo.UpsertAsync(R("2"));
        Action third = () => { _ = repo.UpsertAsync(R("3")); };
        Action fourth = () => { _ = repo.UpsertAsync(R("4")); };
        third.Should().Throw<StorageBusyException>();
        fourth.Should().Throw<StorageBusyException>();

        _probe.Sum(ReservationMetrics.StorageRejectionsName, ("reason", "queue_full")).Should().Be(2);
        _probe.Sum(ReservationMetrics.StorageRejectionsName, ("reason", "busy")).Should().Be(0);
    }

    [Fact]
    public async Task The_queue_depth_gauge_reports_what_is_waiting_and_drops_to_zero_once_drained()
    {
        var batcher = NewBatcher(capacity: 10, start: false);
        var repo = new ReservationRepository(_factory, batcher);

        var pending = Enumerable.Range(0, 4).Select(i => repo.UpsertAsync(R($"r{i}"))).ToList(); // queued, writer not started
        _probe.ObserveGauges();
        _probe.Values(ReservationMetrics.QueueDepthName).Last().Should().Be(4);

        batcher.Start();
        await Task.WhenAll(pending);
        _probe.ObserveGauges();
        _probe.Values(ReservationMetrics.QueueDepthName).Last().Should().Be(0);
    }
}
