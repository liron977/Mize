using Mize.Reservations.Archive.FileSystem;
using Mize.Reservations.Archive.S3;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Mize.Reservations.Retention.Worker;
using Mize.Reservations.Storage.Sqlite;

var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;

builder.Services.AddSingleton(TimeProvider.System);
var policy = RetentionPolicy.From(k => config[k]);
builder.Services.AddSingleton(policy);

// Storage: the same adapter (and shared database) the API uses; the worker has its own group-commit writer.
var storage = config["Storage:Provider"] ?? "Sqlite";
switch (storage.ToLowerInvariant())
{
    case "sqlite": builder.Services.AddSqliteStorage(); break;
    default: throw new InvalidOperationException($"Unknown Storage:Provider '{storage}'. Available: Sqlite.");
}

// Archive: chosen by configuration. The worker is the only host that knows archive adapters (the API never archives).
var archive = config["Archive:Provider"] ?? "None";
switch (archive.ToLowerInvariant())
{
    case "filesystem": builder.Services.AddFileSystemArchive(); break;
    case "s3": builder.Services.AddS3Archive(); break;   // AWS S3 or any S3-compatible server (set Archive:S3:ServiceUrl)
    case "none":
        // Purging with no archive copy must be a deliberate decision, never a default.
        if (policy.Enabled && !(bool.TryParse(config["Retention:AllowDeleteWithoutArchive"], out var allow) && allow))
            throw new InvalidOperationException(
                "Retention is enabled but Archive:Provider is 'None': expired reservations would be deleted with no copy. " +
                "Choose Archive:Provider=FileSystem or S3, or set Retention:AllowDeleteWithoutArchive=true to delete without archiving.");
        builder.Services.AddSingleton<IReservationArchive, DiscardingReservationArchive>();
        break;
    default:
        throw new InvalidOperationException($"Unknown Archive:Provider '{archive}'. Available: None, FileSystem, S3.");
}

builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService<RetentionWorker>();

await builder.Build().RunAsync();
