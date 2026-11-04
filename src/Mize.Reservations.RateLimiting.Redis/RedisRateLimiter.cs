using Microsoft.Extensions.Options;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.RateLimiting;
using StackExchange.Redis;

namespace Mize.Reservations.RateLimiting.Redis;

/// <summary>
/// Exact sliding-window-log limiter. One sorted set per supplier (key = prefix + supplier), so
/// suppliers are isolated by construction. The check-and-record runs as one atomic Lua script and
/// uses Redis TIME, so all app instances share a single clock. Denied requests are NOT recorded.
/// </summary>
public sealed class RedisRateLimiter(IConnectionMultiplexer redis, IOptions<RateLimitOptions> options) : IRateLimiter
{
    private const string Script = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local window = tonumber(ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, now - window)
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[1]) then
            local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
            local retry = tonumber(oldest[2]) + window - now
            if retry < 1 then retry = 1 end
            return {0, retry}
        end
        redis.call('ZADD', KEYS[1], now, ARGV[3])
        redis.call('PEXPIRE', KEYS[1], window)
        return {1, 0}
        """;

    private readonly RateLimitOptions _o = options.Value;

    public async Task<RateLimitDecision> TryAcquireAsync(string supplierKey, CancellationToken ct = default)
    {
        try
        {
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
                Script,
                [(RedisKey)(_o.KeyPrefix + supplierKey)],
                [_o.Limit, _o.WindowSeconds * 1000L, Guid.NewGuid().ToString("N")]))!;

            return (long)result[0] == 1
                ? new RateLimitDecision(true, TimeSpan.Zero)
                : new RateLimitDecision(false, TimeSpan.FromMilliseconds((long)result[1]));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            throw new RateLimiterUnavailableException("Rate limiter backing store is unavailable.", ex);
        }
    }
}
