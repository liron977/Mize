using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mize.Reservations.Core.Abstractions;

namespace Mize.Reservations.Archive.S3;

public static class S3ArchiveServiceCollectionExtensions
{
    /// <summary>
    /// Registers the S3 archive. Configuration (read lazily): <c>Archive:S3:Bucket</c>, <c>Prefix</c>, <c>ServiceUrl</c>
    /// (S3-compatible servers such as MinIO), <c>Region</c>, <c>AccessKey</c>/<c>SecretKey</c> (else the AWS default chain),
    /// <c>ForcePathStyle</c>, <c>CreateBucketIfMissing</c>.
    /// </summary>
    public static IServiceCollection AddS3Archive(this IServiceCollection services)
    {
        services.AddSingleton(sp => Bind(sp.GetRequiredService<IConfiguration>()));

        services.AddSingleton<IAmazonS3>(sp =>
        {
            var o = sp.GetRequiredService<S3ArchiveOptions>();
            var config = new AmazonS3Config
            {
                ForcePathStyle = o.ForcePathStyle ?? !string.IsNullOrWhiteSpace(o.ServiceUrl),
                // Third-party S3 servers: ask the SDK for checksums only where the protocol requires them.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            };

            if (!string.IsNullOrWhiteSpace(o.ServiceUrl))
            {
                config.ServiceURL = o.ServiceUrl;
                config.AuthenticationRegion = o.Region;
            }
            else
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region);
            }

            return !string.IsNullOrWhiteSpace(o.AccessKey) && !string.IsNullOrWhiteSpace(o.SecretKey)
                ? new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), config)
                : new AmazonS3Client(config); // default credential chain (env, profile, instance or task role)
        });

        services.AddSingleton<IReservationArchive, S3ReservationArchive>();
        return services;
    }

    private static S3ArchiveOptions Bind(IConfiguration c)
    {
        string? Get(string k) => c[$"Archive:S3:{k}"] is { Length: > 0 } v ? v : null;
        bool? Bool(string k) => bool.TryParse(Get(k), out var v) ? v : null;

        var o = new S3ArchiveOptions();
        if (Get("Bucket") is { } bucket) o.Bucket = bucket;
        if (Get("Prefix") is { } prefix) o.Prefix = prefix;
        if (Get("Region") is { } region) o.Region = region;
        o.ServiceUrl = Get("ServiceUrl");
        o.AccessKey = Get("AccessKey");
        o.SecretKey = Get("SecretKey");
        o.ForcePathStyle = Bool("ForcePathStyle");
        o.CreateBucketIfMissing = Bool("CreateBucketIfMissing") ?? false;
        return o;
    }
}
