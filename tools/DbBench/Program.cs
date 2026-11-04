// Data-layer benchmark: concurrent upserts straight through WriteBatcher on a real SQLite file.
// No HTTP/nginx/Docker in the path, so it isolates what group commit changes (commits/fsyncs).
// Usage: dotnet run -c Release --project tools/DbBench
using System.Diagnostics;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Storage.Sqlite;

static Reservation R(string sup, string id) => new(sup, id, "room1",
    new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc), 45000,
    new(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));

static async Task<(double rps, double p50, double p95, double p99, double max)> Run(
    SqliteConnectionFactory factory, WriteBatcher batcher, string mode, int concurrency, int total)
{
    var repo = new ReservationRepository(factory, batcher);
    var tag = Guid.NewGuid().ToString("N")[..6];
    if (mode == "dup") await repo.UpsertAsync(R($"s-{tag}", "r")); // the one row everything duplicates
    var lat = new double[total];
    using var gate = new SemaphoreSlim(concurrency);
    var sw = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, total).Select(async i =>
    {
        await gate.WaitAsync();
        try
        {
            var t = Stopwatch.GetTimestamp();
            // dup: identical resend of one reservation; uniq: a new reservation (insert) each time
            await repo.UpsertAsync(mode == "dup" ? R($"s-{tag}", "r") : R($"s-{tag}-{i % 50}", $"r{i}"));
            lat[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        finally { gate.Release(); }
    }));
    sw.Stop();
    Array.Sort(lat);
    double P(double q) => lat[Math.Min(total - 1, (int)(total * q))];
    return (total / sw.Elapsed.TotalSeconds, P(.5), P(.95), P(.99), lat[^1]);
}

var dir = Path.Combine(Path.GetTempPath(), "mize-dbbench-" + Guid.NewGuid().ToString("N"));
Console.WriteLine($"db dir: {dir}  (host disk; synchronous=FULL default, nothing weakened)\n");
Console.WriteLine($"{"batch",-6} {"mode",-5} {"conc",-5} {"n",-6} {"req/s",8} {"p50 ms",8} {"p95 ms",8} {"p99 ms",8} {"max ms",8}");

foreach (var maxBatch in new[] { 1, 256 })
{
    var factory = new SqliteConnectionFactory(Path.Combine(dir, $"b{maxBatch}.db"));
    await DbInitializer.InitializeAsync(factory);
    var batcher = new WriteBatcher(factory, new WriteBatcherOptions { MaxBatchSize = maxBatch, QueueCapacity = 100_000 });
    batcher.Start();
    await Run(factory, batcher, "uniq", 20, 300); // warm-up
    foreach (var mode in new[] { "dup", "uniq" })
        foreach (var conc in new[] { 1, 20, 100 })
        {
            var total = conc == 1 ? 300 : 3000; // sequential is slow with batch=1; keep it short
            var (rps, p50, p95, p99, max) = await Run(factory, batcher, mode, conc, total);
            Console.WriteLine($"{maxBatch,-6} {mode,-5} {conc,-5} {total,-6} {rps,8:N0} {p50,8:N2} {p95,8:N2} {p99,8:N2} {max,8:N1}");
        }
    await batcher.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
try { Directory.Delete(dir, true); } catch { }
