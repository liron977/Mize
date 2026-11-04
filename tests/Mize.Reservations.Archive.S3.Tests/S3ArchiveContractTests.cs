using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Mize.Reservations.Archive.S3;
using Mize.Reservations.Contracts;
using Mize.Reservations.Core.Abstractions;

namespace Mize.Reservations.Archive.S3.Tests;

/// <summary>
/// Runs the archive contract against a real S3-compatible endpoint that YOU provide, so the same tests work against MinIO (or
/// another S3-compatible server) and against real AWS S3. Nothing is started for you; without configuration every test is
/// reported as skipped, with instructions.
///
///   MIZE_S3_ENDPOINT    e.g. http://localhost:9000   (leave unset to use AWS S3 with the default credential chain)
///   MIZE_S3_ACCESS_KEY  access key  (optional for AWS: the default chain is used when absent)
///   MIZE_S3_SECRET_KEY  secret key
///   MIZE_S3_BUCKET      bucket to use (default: mize-archive-test; created if missing when an endpoint is set)
///   MIZE_S3_REGION      region (default us-east-1)
///
/// Every test writes under its own random key prefix and removes it afterwards, so one bucket can be shared.
/// </summary>
public sealed class S3ArchiveContractTests : ArchiveContract
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static readonly bool Configured = Env("MIZE_S3_ENDPOINT") is not null || Env("MIZE_S3_BUCKET") is not null;

    protected override string? SkipReason => Configured ? null :
        "No S3 server configured. Set MIZE_S3_ENDPOINT (and MIZE_S3_ACCESS_KEY / MIZE_S3_SECRET_KEY) to run these against " +
        "MinIO or another S3-compatible server, or MIZE_S3_BUCKET to run against AWS S3. See the README, Data retention.";

    protected override Task<(IReservationArchive Archive, Func<Task>? Cleanup)> CreateAsync()
    {
        var endpoint = Env("MIZE_S3_ENDPOINT");
        var region = Env("MIZE_S3_REGION") ?? "us-east-1";
        var bucket = Env("MIZE_S3_BUCKET") ?? "mize-archive-test";
        var prefix = $"tests/{Guid.NewGuid():N}/";

        var config = new AmazonS3Config
        {
            ForcePathStyle = endpoint is not null,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        if (endpoint is not null) { config.ServiceURL = endpoint; config.AuthenticationRegion = region; }
        else config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);

        IAmazonS3 s3 = Env("MIZE_S3_ACCESS_KEY") is { } key && Env("MIZE_S3_SECRET_KEY") is { } secret
            ? new AmazonS3Client(new BasicAWSCredentials(key, secret), config)
            : new AmazonS3Client(config);

        var archive = new S3ReservationArchive(s3, new S3ArchiveOptions
        {
            Bucket = bucket,
            Prefix = prefix,
            CreateBucketIfMissing = endpoint is not null // convenient for a local server; never implicit on AWS
        });

        async Task Cleanup()
        {
            var keys = new List<KeyVersion>();
            string? token = null;
            do
            {
                var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, ContinuationToken = token });
                keys.AddRange((page.S3Objects ?? []).Select(o => new KeyVersion { Key = o.Key }));
                token = page.IsTruncated == true ? page.NextContinuationToken : null;
            }
            while (token is not null);

            foreach (var chunk in keys.Chunk(500))
                await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = chunk.ToList() });
            s3.Dispose();
        }

        return Task.FromResult<(IReservationArchive, Func<Task>?)>((archive, Cleanup));
    }
}
