using Microsoft.Extensions.Options;
using Mize.Reservations.Contracts;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.RateLimiting;
using Mize.Reservations.RateLimiting.InMemory;
using Mize.Reservations.RateLimiting.Redis;
using StackExchange.Redis;
using Xunit;

namespace Mize.Reservations.IntegrationTests;

/// <summary>Shared plumbing for the Redis-backed variants: separate multiplexers per "instance", one shared key space.</summary>
public abstract class RedisLimiterContractBase(RedisFixture redis) : DistributedRateLimiterContract
{
    private readonly List<ConnectionMultiplexer> _connections = new();

    protected abstract IRateLimiter Wrap(RedisRateLimiter redisLimiter);

    protected override IReadOnlyList<IRateLimiter> CreateSharedLimiters(int limit, int windowSeconds, int count)
    {
        var options = Options.Create(new RateLimitOptions
        {
            Limit = limit, WindowSeconds = windowSeconds, KeyPrefix = $"t{Guid.NewGuid():N}:"
        });
        return Enumerable.Range(0, count).Select(_ =>
        {
            var mux = ConnectionMultiplexer.Connect(redis.ConnectionString); // a separate connection per instance
            _connections.Add(mux);
            return Wrap(new RedisRateLimiter(mux, options));
        }).ToList();
    }

    public override async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.CloseAsync();
    }
}

/// <summary>The Redis limiter on its own.</summary>
[Collection(RedisCollection.Name)]
public sealed class RedisRateLimiterContractTests(RedisFixture redis) : RedisLimiterContractBase(redis)
{
    protected override IRateLimiter Wrap(RedisRateLimiter redisLimiter) => redisLimiter;
}

/// <summary>The exact composition the app runs: Redis behind the local block cache. Must satisfy the same contract.</summary>
[Collection(RedisCollection.Name)]
public sealed class BlockCachedRedisRateLimiterContractTests(RedisFixture redis) : RedisLimiterContractBase(redis)
{
    protected override IRateLimiter Wrap(RedisRateLimiter redisLimiter) => new BlockCachingRateLimiter(redisLimiter);
}
