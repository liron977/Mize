using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mize.Reservations.IntegrationTests;

/// <summary>An API "instance" wired to a given Redis and SQLite file.</summary>
public sealed class ApiInstance(string redis, string dbPath, string prefix, int limit = 100, int window = 60, string? rateLimitProvider = null,
    Action<IServiceCollection>? configureServices = null, string? storageProvider = null,
    IDictionary<string, string?>? extraConfig = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (rateLimitProvider is not null) builder.UseSetting("RateLimit:Provider", rateLimitProvider); // read at startup, so must be early
        if (storageProvider is not null) builder.UseSetting("Storage:Provider", storageProvider);
        if (configureServices is not null) builder.ConfigureTestServices(configureServices);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Redis:ConnectionString"] = redis,
            ["Sqlite:Path"] = dbPath,
            ["RateLimit:Limit"] = limit.ToString(),
            ["RateLimit:WindowSeconds"] = window.ToString(),
            ["RateLimit:KeyPrefix"] = prefix
        }));
        if (extraConfig is not null)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(extraConfig));
            foreach (var (key, value) in extraConfig)
                if (value is not null) builder.UseSetting(key, value); // early: log levels and other startup-time settings must see it
        }
    }
}
