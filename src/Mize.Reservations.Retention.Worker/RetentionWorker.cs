using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;

namespace Mize.Reservations.Retention.Worker;

/// <summary>
/// Runs <see cref="RetentionService"/>: once and exit (the default; schedule it with cron, a Kubernetes CronJob, ...), or on an
/// interval when <c>Retention:RunIntervalMinutes</c> is greater than zero. One-shot runs set a non-zero exit code on failure.
/// </summary>
public sealed class RetentionWorker(
    RetentionService retention,
    RetentionPolicy policy,
    IConfiguration config,
    IEnumerable<IStorageInitializer> initializers,
    IHostApplicationLifetime lifetime,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var init in initializers) await init.InitializeAsync(stoppingToken); // the worker may start before the API

            if (!policy.Enabled)
            {
                logger.LogWarning("Retention is disabled (Retention:Enabled=false): nothing to do.");
                return;
            }

            var interval = int.TryParse(config["Retention:RunIntervalMinutes"], out var m) && m > 0 ? TimeSpan.FromMinutes(m) : (TimeSpan?)null;
            logger.LogInformation(
                "Retention worker started: keep stays for {Days} days, batch {Batch}, dry run {DryRun}, {Mode}.",
                policy.RetentionDays, policy.BatchSize, policy.DryRun, interval is null ? "one run" : $"every {interval}");

            do
            {
                try
                {
                    var r = await retention.RunOnceAsync(stoppingToken);
                    logger.LogInformation(
                        "Retention run finished (cutoff {Cutoff:O}, dry run {DryRun}): {Batches} pages, {Archived} rows {Verb}, {Objects} archive objects, {Deleted} deleted, {Skipped} skipped because they changed.",
                        r.CutoffUtc, r.DryRun, r.Batches, r.Archived, r.DryRun ? "would be archived" : "archived", r.ArchiveObjects, r.Deleted, r.SkippedBecauseChanged);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Retention run failed. Nothing is deleted before its archive copy is durable, so the next run is safe.");
                    if (interval is null) Environment.ExitCode = 1;
                }

                if (interval is null) break;
                await Task.Delay(interval.Value, stoppingToken);
            }
            while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Retention worker failed to start.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
