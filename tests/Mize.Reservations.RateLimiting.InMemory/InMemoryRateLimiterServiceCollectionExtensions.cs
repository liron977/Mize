using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mize.Reservations.Core.Abstractions;

namespace Mize.Reservations.RateLimiting.InMemory;

public static class InMemoryRateLimiterServiceCollectionExtensions
{
    /// <summary>
    /// Registers the verification-only in-process limiter. Deliberately NOT wired into the Api's
    /// <c>InfrastructureRegistration</c>; tests opt in by replacing the <see cref="IRateLimiter"/> port.
    /// </summary>
    public static IServiceCollection AddInMemoryRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimitOptions();
        services.AddSingleton<IRateLimiter>(sp => new InMemoryRateLimiter(sp.GetRequiredService<IOptions<RateLimitOptions>>()));
        return services;
    }
}
