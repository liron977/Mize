using System.Reflection;
using FluentAssertions;
using Mize.Reservations.Archive.FileSystem;
using Mize.Reservations.Archive.S3;
using Mize.Reservations.Core.Models;
using Mize.Reservations.RateLimiting;
using Mize.Reservations.RateLimiting.InMemory;
using Mize.Reservations.RateLimiting.Redis;
using Mize.Reservations.Retention.Worker;
using Mize.Reservations.Storage.InMemory;
using Mize.Reservations.Storage.Sqlite;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>
/// Executable architecture rules: they fail the build the moment someone couples Core to an engine, couples one adapter
/// to another, lets an engine package leak out of its adapter, or wires a verification-only adapter into the product.
/// Reference lists are read from the compiled assemblies, so this checks real dependencies, not intentions.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Core = typeof(Reservation).Assembly;
    private static readonly Assembly Sqlite = typeof(WriteBatcher).Assembly;
    private static readonly Assembly Limiting = typeof(RateLimitOptions).Assembly;
    private static readonly Assembly Redis = typeof(RedisRateLimiter).Assembly;
    private static readonly Assembly FileArchive = typeof(FileSystemReservationArchive).Assembly;
    private static readonly Assembly S3Archive = typeof(S3ReservationArchive).Assembly;
    private static readonly Assembly Worker = typeof(RetentionWorker).Assembly;
    private static readonly Assembly Api = typeof(Mize.Reservations.Api.Composition.InfrastructureRegistration).Assembly;

    // verification-only adapters (live under tests/)
    private static readonly Assembly InMemoryLimiter = typeof(InMemoryRateLimiter).Assembly;
    private static readonly Assembly InMemoryStorage = typeof(InMemoryStore).Assembly;

    private static readonly string[] EnginePackages = ["Dapper", "Microsoft.Data.Sqlite", "StackExchange.Redis", "AWSSDK.S3", "AWSSDK.Core"];

    private static HashSet<string> Refs(Assembly a) =>
        a.GetReferencedAssemblies().Select(r => r.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> MizeRefs(Assembly a) =>
        Refs(a).Where(n => n.StartsWith("Mize.Reservations.", StringComparison.OrdinalIgnoreCase));

    private static string Name(Assembly a) => a.GetName().Name!;

    [Fact]
    public void Positive_control_the_scan_sees_the_dependencies_we_know_exist()
    {
        // If these failed, the rules below could "pass" simply because the scan sees nothing.
        Refs(Sqlite).Should().Contain(["Dapper", "Microsoft.Data.Sqlite"]);
        Refs(Redis).Should().Contain("StackExchange.Redis");
        Refs(S3Archive).Should().Contain("AWSSDK.S3");
        MizeRefs(Sqlite).Should().Contain(Name(Core));
        MizeRefs(Redis).Should().Contain([Name(Core), Name(Limiting)]);
        MizeRefs(S3Archive).Should().Contain(Name(Core));
        MizeRefs(FileArchive).Should().Contain(Name(Core));
        MizeRefs(InMemoryStorage).Should().Contain(Name(Core));
        MizeRefs(InMemoryLimiter).Should().Contain([Name(Core), Name(Limiting)]);
        MizeRefs(Api).Should().Contain([Name(Core), Name(Sqlite), Name(Redis)]);
        MizeRefs(Worker).Should().Contain([Name(Core), Name(Sqlite), Name(FileArchive), Name(S3Archive)]);
    }

    [Fact]
    public void Core_depends_on_no_other_project_and_no_engine_or_host_package()
    {
        MizeRefs(Core).Should().BeEmpty("Core is the center: dependencies point inward only");
        Refs(Core).Should().NotContain(EnginePackages);
        Refs(Core).Where(n => n.StartsWith("Microsoft.AspNetCore")).Should().BeEmpty("Core must not know about the HTTP host");
    }

    [Fact]
    public void Adapters_depend_on_Core_and_never_on_each_other_or_on_a_host()
    {
        var allowed = new Dictionary<Assembly, string[]>
        {
            [Sqlite] = [Name(Core)],
            [Limiting] = [Name(Core)],
            [Redis] = [Name(Core), Name(Limiting)],           // the Redis limiter builds on the generic limiter project
            [FileArchive] = [Name(Core)],
            [S3Archive] = [Name(Core)],
            [InMemoryStorage] = [Name(Core)],                 // verification adapter: Core and nothing else
            [InMemoryLimiter] = [Name(Core), Name(Limiting)], // verification adapter: Core + the shared options type
        };

        foreach (var (adapter, allowedRefs) in allowed)
            MizeRefs(adapter).Should().BeSubsetOf(allowedRefs, $"{Name(adapter)} may only depend on {string.Join(", ", allowedRefs)}");
    }

    [Fact]
    public void Each_engine_package_is_confined_to_its_own_adapter()
    {
        Refs(Sqlite).Should().NotContain(["StackExchange.Redis", "AWSSDK.S3"]);
        Refs(Redis).Should().NotContain(["Dapper", "Microsoft.Data.Sqlite", "AWSSDK.S3"]);
        Refs(S3Archive).Should().NotContain(["Dapper", "Microsoft.Data.Sqlite", "StackExchange.Redis"]);
        Refs(FileArchive).Should().NotContain(EnginePackages, "the directory archive needs no engine package at all");
        Refs(Limiting).Should().NotContain(EnginePackages, "the generic limiter project must stay engine-free");
        Refs(InMemoryStorage).Should().NotContain(EnginePackages, "the verification adapter uses no real engine at all");
        Refs(InMemoryLimiter).Should().NotContain(EnginePackages, "the verification adapter uses no real engine at all");

        // hosts compose adapters; they never use an engine's types themselves
        Refs(Api).Should().NotContain(["Dapper", "Microsoft.Data.Sqlite", "AWSSDK.S3"]);
        Refs(Worker).Should().NotContain(EnginePackages);
    }

    [Fact]
    public void Telemetry_exporters_live_only_in_the_api_host_Core_records_metrics_with_framework_types_only()
    {
        // Core and the adapters record standard System.Diagnostics.Metrics instruments (part of the framework); only the host
        // chooses where they go. So no OpenTelemetry (or any exporter) assembly may be referenced outside the API.
        Refs(Api).Should().Contain(n => n.StartsWith("OpenTelemetry"), "the API wires the Prometheus exporter");

        foreach (var assembly in new[] { Core, Sqlite, Limiting, Redis, FileArchive, S3Archive, Worker, InMemoryStorage, InMemoryLimiter })
            Refs(assembly).Where(n => n.StartsWith("OpenTelemetry"))
                .Should().BeEmpty($"{Name(assembly)} must not depend on a telemetry exporter");
    }

    [Fact]
    public void The_api_never_archives_and_the_worker_never_serves_traffic()
    {
        // Archiving is the worker's job (so several API instances can't race on retention); the API only needs the policy.
        MizeRefs(Api).Should().NotContain([Name(FileArchive), Name(S3Archive), Name(Worker)]);

        MizeRefs(Worker).Should().NotContain(Name(Api));
        MizeRefs(Worker).Should().NotContain([Name(Limiting), Name(Redis)], "the worker does no rate limiting");
    }

    [Fact]
    public void The_product_never_references_the_verification_only_in_memory_adapters()
    {
        // Supported engines in the product: SQLite (storage), Redis (rate limiting) and, for the archive, the directory and
        // S3 adapters. The in-memory adapters exist only under tests/ to prove the ports are swappable.
        foreach (var verificationOnly in new[] { InMemoryStorage, InMemoryLimiter })
            foreach (var productAssembly in new[] { Api, Core, Sqlite, Limiting, Redis, FileArchive, S3Archive, Worker })
                MizeRefs(productAssembly).Should().NotContain(Name(verificationOnly),
                    $"{Name(productAssembly)} must not reference the verification-only {Name(verificationOnly)}");
    }
}
