using Microsoft.Extensions.Logging;
using Mize.Reservations.Core.Models;

namespace Mize.Reservations.Api.Observability;

/// <summary>One line at startup saying which adapters and policies this instance runs with: the first thing to read when something is off.</summary>
public static partial class StartupLog
{
    public static void Summary(ILogger logger, IConfiguration config, RetentionPolicy retention) =>
        Started(logger,
            config["Storage:Provider"] ?? "Sqlite",
            config["RateLimit:Provider"] ?? "Redis",
            config["RateLimit:Limit"] ?? "100",
            config["RateLimit:WindowSeconds"] ?? "60",
            config["Sqlite:MaxBatchSize"] ?? "256",
            config["Sqlite:QueueCapacity"] ?? "2000",
            retention.Enabled,
            retention.RetentionDays);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Reservation ingest API started: storage={Storage}, rateLimiter={RateLimiter} ({Limit} per {WindowSeconds}s), writeBatch={BatchSize}, writeQueue={QueueCapacity}, retention={RetentionEnabled} ({RetentionDays} days)")]
    private static partial void Started(ILogger logger, string storage, string rateLimiter, string limit, string windowSeconds,
        string batchSize, string queueCapacity, bool retentionEnabled, int retentionDays);
}
