namespace Mize.Reservations.RateLimiting;

/// <summary>Provider-neutral limiter settings (config section <c>RateLimit</c>).</summary>
public sealed class RateLimitOptions
{
    /// <summary>Max accepted requests per supplier in any rolling window.</summary>
    public int Limit { get; set; } = 100;

    public int WindowSeconds { get; set; } = 60;

    /// <summary>Namespace for keys in a shared store; ignored by providers that keep state in-process.</summary>
    public string KeyPrefix { get; set; } = "ratelimit:";
}
