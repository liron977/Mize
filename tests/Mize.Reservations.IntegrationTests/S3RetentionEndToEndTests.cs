using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Mize.Reservations.Archive.S3;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;
using Xunit;

namespace Mize.Reservations.IntegrationTests;

/// <summary>
/// Connection to the S3-compatible server the S3 tests run against, taken from the same environment variables as
/// <c>tests/Mize.Reservations.Archive.S3.Tests</c>:
///   MIZE_S3_ENDPOINT / MIZE_S3_ACCESS_KEY / MIZE_S3_SECRET_KEY / MIZE_S3_BUCKET / MIZE_S3_REGION.
/// Every instance writes under its own random key prefix and removes it on dispose, so one bucket can be shared.
/// </summary>
internal sealed class S3Env : IAsyncDisposable
{
    public const string SkipMessage =
        "No S3 server configured. Set MIZE_S3_ENDPOINT (and MIZE_S3_ACCESS_KEY / MIZE_S3_SECRET_KEY) to run against MinIO or another " +
        "S3-compatible server, or MIZE_S3_BUCKET to run against AWS S3. See docs/DESIGN.md, Data retention.";

    private static string? Var(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    public static bool Configured => Var("MIZE_S3_ENDPOINT") is not null || Var("MIZE_S3_BUCKET") is not null;

    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly bool _cleanup;

    public S3ReservationArchive Archive { get; }

    private S3Env(IAmazonS3 client, string bucket, string prefix, bool createBucket, bool cleanup)
    {
        _client = client; _bucket = bucket; _prefix = prefix; _cleanup = cleanup;
        Archive = new S3ReservationArchive(client, new S3ArchiveOptions { Bucket = bucket, Prefix = prefix, CreateBucketIfMissing = createBucket });
    }

    /// <summary>The configured server, under a fresh private prefix.</summary>
    public static S3Env Create()
    {
        var endpoint = Var("MIZE_S3_ENDPOINT");
        var region = Var("MIZE_S3_REGION") ?? "us-east-1";
        var config = new AmazonS3Config
        {
            ForcePathStyle = endpoint is not null,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        if (endpoint is not null) { config.ServiceURL = endpoint; config.AuthenticationRegion = region; }
        else config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);

        IAmazonS3 client = Var("MIZE_S3_ACCESS_KEY") is { } key && Var("MIZE_S3_SECRET_KEY") is { } secret
            ? new AmazonS3Client(new BasicAWSCredentials(key, secret), config)
            : new AmazonS3Client(config);

        return new S3Env(client, Var("MIZE_S3_BUCKET") ?? "mize-archive-test", $"tests/e2e/{Guid.NewGuid():N}/",
            createBucket: endpoint is not null, cleanup: true);
    }

    /// <summary>A server that refuses connections (nothing listens on port 1), with no retries so the failure is immediate.</summary>
    public static S3Env Unreachable() =>
        new(new AmazonS3Client(new BasicAWSCredentials("x", "y"),
                new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true, AuthenticationRegion = "us-east-1", MaxErrorRetry = 0 }),
            "no-such-bucket", "tests/", createBucket: false, cleanup: false);

    public Task<IReadOnlyList<string>> Keys() => Archive.ListAsync(ArchiveLayout.Root);

    public async Task<List<Reservation>> Everything()
    {
        var all = new List<Reservation>();
        foreach (var key in await Keys()) all.AddRange(await Archive.ReadAsync(key));
        return all;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cleanup)
        {
            var keys = new List<KeyVersion>();
            string? token = null;
            do
            {
                var page = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = _prefix, ContinuationToken = token });
                keys.AddRange((page.S3Objects ?? []).Select(o => new KeyVersion { Key = o.Key }));
                token = page.IsTruncated == true ? page.NextContinuationToken : null;
            }
            while (token is not null);

            foreach (var chunk in keys.Chunk(500))
                await _client.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = _bucket, Objects = chunk.ToList() });
        }
        _client.Dispose();
    }
}

/// <summary>
/// The retention flow on real SQLite with the S3 archive: the same scenarios as <see cref="RetentionEndToEndTests"/> (which uses
/// the directory archive), so the backend is the only difference. Skipped, with instructions, unless an S3 server is configured;
/// the unreachable-server test needs no server.
/// </summary>
public sealed class S3RetentionEndToEndTests
{
    private static Reservation R(string supplier, string id, DateTime checkOut, long price = 10000, int updatedDay = 1) =>
        RetentionHarness.R(supplier, id, checkOut, price, updatedDay);

    private static IEnumerable<Reservation> ByKey(IEnumerable<Reservation> rows) =>
        rows.OrderBy(r => r.SupplierId, StringComparer.Ordinal).ThenBy(r => r.ReservationId, StringComparer.Ordinal);

    [SkippableFact]
    public async Task Old_reservations_move_to_S3_and_leave_the_database_while_recent_ones_stay()
    {
        Skip.IfNot(S3Env.Configured, S3Env.SkipMessage);
        await using var h = await RetentionHarness.CreateAsync();
        await using var s3 = S3Env.Create();
        var old = new[] { R("acme", "o1", RetentionHarness.OldStay), R("acme", "o2", RetentionHarness.OldStay), R("other", "o3", RetentionHarness.OldStay) };
        await h.Seed(old);
        await h.Seed(R("acme", "n1", RetentionHarness.RecentStay), R("other", "n2", RetentionHarness.RecentStay));
        var statsBefore = await h.Stats.GetAsync("acme");

        var result = await h.Service(archive: s3.Archive).RunOnceAsync();

        result.Archived.Should().Be(3);
        result.Deleted.Should().Be(3);
        (await h.RowCount()).Should().Be(2);
        (await h.Repo.GetAsync("acme", "n1")).Should().NotBeNull();
        (await h.Repo.GetAsync("acme", "o1")).Should().BeNull();
        (await s3.Keys()).Should().HaveCount(2).And.OnlyContain(k => k.StartsWith("reservations/v1/checkout=2024-03/supplier="));
        ByKey(await s3.Everything()).Should().Equal(ByKey(old));                      // S3 holds exactly what left the database
        (await h.Stats.GetAsync("acme")).Should().Be(statsBefore);                    // lifetime counters untouched

        var keysAfterFirst = await s3.Keys();
        var again = await h.Service(archive: s3.Archive).RunOnceAsync();               // idempotent: nothing left to do
        (again.Archived, again.Deleted).Should().Be((0, 0));
        (await s3.Keys()).Should().Equal(keysAfterFirst);
    }

    [SkippableFact]
    public async Task A_crash_after_archiving_to_S3_loses_nothing_and_the_rerun_overwrites_the_same_keys()
    {
        Skip.IfNot(S3Env.Configured, S3Env.SkipMessage);
        await using var h = await RetentionHarness.CreateAsync();
        await using var s3 = S3Env.Create();
        var old = Enumerable.Range(0, 20).Select(i => R(i % 2 == 0 ? "acme" : "other", $"o{i:D2}", RetentionHarness.OldStay)).ToArray();
        await h.Seed(old);
        var faulty = new FaultyStore(h.Store) { FailNextDelete = true };

        var crash = async () => await h.Service(store: faulty, archive: s3.Archive).RunOnceAsync();
        await crash.Should().ThrowAsync<IOException>();

        (await h.RowCount()).Should().Be(20);                                         // nothing deleted
        var keys = await s3.Keys();
        keys.Should().NotBeEmpty();                                                   // but the objects are already durable in S3

        var rerun = await h.Service(store: faulty, archive: s3.Archive).RunOnceAsync();

        rerun.Deleted.Should().Be(20);
        (await h.RowCount()).Should().Be(0);
        (await s3.Keys()).Should().Equal(keys);                                       // same keys overwritten, none added
        ByKey(await s3.Everything()).Should().Equal(ByKey(old));                      // each reservation archived exactly once
    }

    [SkippableFact]
    public async Task A_supplier_update_between_archive_and_delete_keeps_the_live_reservation_when_the_archive_is_S3()
    {
        Skip.IfNot(S3Env.Configured, S3Env.SkipMessage);
        await using var h = await RetentionHarness.CreateAsync();
        await using var s3 = S3Env.Create();
        await h.Seed(R("acme", "stays-live", RetentionHarness.OldStay, price: 10000), R("acme", "goes", RetentionHarness.OldStay));
        var faulty = new FaultyStore(h.Store)
        {
            BeforeDelete = _ => h.Repo.UpsertAsync(R("acme", "stays-live", RetentionHarness.RecentStay, price: 99999, updatedDay: 20))
        };

        var result = await h.Service(store: faulty, archive: s3.Archive).RunOnceAsync();

        result.Deleted.Should().Be(1);
        result.SkippedBecauseChanged.Should().Be(1);
        (await h.Repo.GetAsync("acme", "stays-live"))!.PriceCents.Should().Be(99999);  // the live, updated reservation survived
        (await h.Repo.GetAsync("acme", "goes")).Should().BeNull();
        (await s3.Everything()).Select(r => r.ReservationId).Should().BeEquivalentTo("stays-live", "goes"); // both were archived first
    }

    [SkippableFact]
    public async Task Thousands_of_rows_move_to_S3_in_batches()
    {
        Skip.IfNot(S3Env.Configured, S3Env.SkipMessage);
        await using var h = await RetentionHarness.CreateAsync(new RetentionPolicy(Enabled: true, RetentionDays: 365, BatchSize: 500));
        await using var s3 = S3Env.Create();
        var old = Enumerable.Range(0, 3000).Select(i => R(i % 7 == 0 ? "acme" : "other", $"o{i:D5}", RetentionHarness.OldStay.AddDays(i % 40))).ToArray();
        await h.Seed(old);

        var result = await h.Service(archive: s3.Archive).RunOnceAsync();

        result.Batches.Should().Be(6);
        result.Deleted.Should().Be(3000);
        (await h.RowCount()).Should().Be(0);
        ByKey(await s3.Everything()).Should().Equal(ByKey(old));
    }

    [Fact]
    public async Task When_the_S3_server_cannot_be_reached_nothing_is_deleted()
    {
        // needs no server: nothing listens on port 1
        await using var h = await RetentionHarness.CreateAsync();
        await using var s3 = S3Env.Unreachable();
        await h.Seed(R("acme", "o1", RetentionHarness.OldStay), R("acme", "o2", RetentionHarness.OldStay));

        var act = async () => await h.Service(archive: s3.Archive).RunOnceAsync();

        await act.Should().ThrowAsync<Exception>();
        (await h.RowCount()).Should().Be(2);                                          // the archive write failed, so no row was deleted
        (await h.Repo.GetAsync("acme", "o1")).Should().NotBeNull();
    }
}
