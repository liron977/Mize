using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mize.Reservations.RateLimiting;

public static class RateLimitingServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="RateLimitOptions"/> from the <c>RateLimit</c> section. Read lazily (when first resolved),
    /// so hosts and tests can override configuration. Every provider calls this.
    /// </summary>
    public static IServiceCollection AddRateLimitOptions(this IServiceCollection services)
    {
        services.AddOptions<RateLimitOptions>().Configure<IConfiguration>((o, c) =>
        {
            if (int.TryParse(c["RateLimit:Limit"], out var limit)) o.Limit = limit;
            if (int.TryParse(c["RateLimit:WindowSeconds"], out var window)) o.WindowSeconds = window;
            if (c["RateLimit:KeyPrefix"] is { Length: > 0 } prefix) o.KeyPrefix = prefix;
        });
        return services;
    }
}
