using Mize.Reservations.Api.Composition;
using Mize.Reservations.Api.Endpoints;
using Mize.Reservations.Api.Middleware;
using Mize.Reservations.Api.Observability;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Mize.Reservations.Core.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

// Infrastructure adapters (storage, rate limiter), chosen by configuration. Nothing below names an engine.
builder.Services.AddInfrastructure(builder.Configuration);

// Retention policy (disabled by default) and clock, for the resurrection guard: stays older than the retention cutoff are
// ignored on ingest (outcome "Expired"), because they may already have been archived and purged.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => RetentionPolicy.From(k => sp.GetRequiredService<IConfiguration>()[k]));

// Observability. The instruments are standard .NET metrics owned by Core (no vendor there); the API host decides where they
// go. Logging is the framework's structured ILogger (set Logging__Console__FormatterName=json for machine-readable lines).
builder.Services.AddSingleton<ReservationMetrics>();   // picks up the host's IMeterFactory
builder.Services.AddSingleton<LogSampler>();
builder.Services.AddSingleton<IngestLogger>();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("reservation-ingest-api"))
    .WithMetrics(metrics => metrics
        .AddMeter(ReservationMetrics.MeterName)
        .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel") // built-in HTTP server metrics
        .AddRuntimeInstrumentation()                                                      // GC, thread pool, exceptions, ...
        // Histograms recorded in SECONDS need seconds-scale buckets; the default buckets assume milliseconds and would put
        // every ingest in the first bucket.
        .AddView(ReservationMetrics.IngestDurationName, new ExplicitBucketHistogramConfiguration
            { Boundaries = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10] })
        .AddView(ReservationMetrics.BatchCommitDurationName, new ExplicitBucketHistogramConfiguration
            { Boundaries = [0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1] })
        .AddView(ReservationMetrics.BatchSizeName, new ExplicitBucketHistogramConfiguration
            { Boundaries = [1, 2, 5, 10, 25, 50, 100, 256, 512] })
        .AddPrometheusExporter());

// Application services: depend only on the ports in Core.
builder.Services.AddSingleton<IngestService>();
builder.Services.AddSingleton<StatsService>();

var app = builder.Build();

// One-time storage setup (schema/migrations), whichever engine is registered.
foreach (var initializer in app.Services.GetServices<IStorageInitializer>())
    await initializer.InitializeAsync();

StartupLog.Summary(app.Logger, app.Configuration, app.Services.GetRequiredService<RetentionPolicy>());

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.MapGet("/health", () => Results.Ok("ok"));
app.MapPrometheusScrapingEndpoint();   // GET /metrics (Prometheus text). Not for the public internet: the proxy blocks it.
app.MapReservations();

app.Run();

public partial class Program;
