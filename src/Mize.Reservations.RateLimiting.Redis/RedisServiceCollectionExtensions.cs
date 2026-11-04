using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Telemetry;
using Mize.Reservations.RateLimiting;
using StackExchange.Redis;

namespace Mize.Reservations.RateLimiting.Redis;

public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Redis-backed limiter (shared across instances) behind a local block cache.
    /// Configuration (read lazily): <c>Redis:ConnectionString</c> plus the provider-neutral <c>RateLimit:*</c> keys.
    /// Any Redis-protocol service works (self-hosted, ElastiCache, Azure Cache for Redis, Memorystore, ...).
    /// </summary>
    public static IServiceCollection AddRedisRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimitOptions();

        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var cs = sp.GetRequiredService<IConfiguration>()["Redis:ConnectionString"] ?? "localhost:6379";
            var options = ConfigurationOptions.Parse(cs);
            options.AbortOnConnectFail = false;   // start even if Redis is briefly down; calls fail closed (503)
            options.ConnectTimeout = 2000;
            options.AsyncTimeout = 500;
            options.SyncTimeout = 500;
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddSingleton<RedisRateLimiter>();

        // A throttled supplier is rejected locally until its window frees a slot, so a flood costs no Redis calls.
        services.AddSingleton<IRateLimiter>(sp => new BlockCachingRateLimiter(sp.GetRequiredService<RedisRateLimiter>(), metrics: sp.GetService<ReservationMetrics>()));
        return services;
    }
}
