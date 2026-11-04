using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mize.Reservations.Core.Abstractions;

namespace Mize.Reservations.Storage.Sqlite;

public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite storage adapter: <see cref="IReservationRepository"/>, <see cref="IStatsRepository"/>,
    /// <see cref="IStorageInitializer"/> and the group-commit writer. Configuration (read lazily, so hosts and tests can
    /// override it): <c>Sqlite:Path</c>, <c>Sqlite:BusyTimeoutSeconds</c> (how long a write waits for a lock held by another process before
    /// answering 503), <c>Sqlite:MaxBatchSize</c> (1 = no batching), <c>Sqlite:QueueCapacity</c>.
    /// </summary>
    public static IServiceCollection AddSqliteStorage(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return new SqliteConnectionFactory(config["Sqlite:Path"] ?? "data/reservations.db", Int(config, "Sqlite:BusyTimeoutSeconds", 10));
        });

        services.AddSingleton(sp =>
        {
            var c = sp.GetRequiredService<IConfiguration>();
            return new WriteBatcherOptions
            {
                MaxBatchSize = Int(c, "Sqlite:MaxBatchSize", 256),
                QueueCapacity = Int(c, "Sqlite:QueueCapacity", 2000)
            };
        });

        // Group commit: one writer loop per instance commits many requests per transaction (one fsync for all).
        services.AddSingleton<WriteBatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<WriteBatcher>());

        services.AddSingleton<IReservationRepository, ReservationRepository>();
        services.AddSingleton<IStatsRepository, StatsRepository>();
        services.AddSingleton<IReservationRetentionStore, SqliteRetentionStore>();
        services.AddSingleton<IStorageInitializer, SqliteStorageInitializer>();
        return services;
    }

    private static int Int(IConfiguration c, string key, int fallback) =>
        int.TryParse(c[key], out var v) ? v : fallback;
}
