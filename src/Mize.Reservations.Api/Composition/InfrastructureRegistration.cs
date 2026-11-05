using Mize.Reservations.RateLimiting.Redis;
using Mize.Reservations.Storage.Sqlite;

namespace Mize.Reservations.Api.Composition;

/// <summary>
/// The ONLY place that knows which concrete adapters exist. Each port is bound to one adapter by configuration,
/// so the application (Core + endpoints) never names an engine.
///
/// To add an engine: implement the port in its own project, give it an <c>AddXxx(IServiceCollection)</c> extension,
/// add one case below, and run the shared contract tests against it (see docs/DESIGN.md "Extension points").
/// </summary>
public static class InfrastructureRegistration
{
    public const string StorageProviderKey = "Storage:Provider";
    public const string RateLimitProviderKey = "RateLimit:Provider";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Ports: IReservationRepository, IStatsRepository, IStorageInitializer
        var storage = config[StorageProviderKey] ?? "Sqlite";
        switch (storage.ToLowerInvariant())
        {
            case "sqlite":
                services.AddSqliteStorage();
                break;
            default:
                throw new InvalidOperationException($"Unknown {StorageProviderKey} '{storage}'. Available: Sqlite.");
        }

        // Port: IRateLimiter. Redis is the only supported engine: its state is shared across instances, which the
        // limit requires. (An in-process limiter exists only under tests/ to verify the port; it is not selectable.)
        var limiter = config[RateLimitProviderKey] ?? "Redis";
        switch (limiter.ToLowerInvariant())
        {
            case "redis":
                services.AddRedisRateLimiting();
                break;
            default:
                throw new InvalidOperationException($"Unknown {RateLimitProviderKey} '{limiter}'. Available: Redis.");
        }

        return services;
    }
}
