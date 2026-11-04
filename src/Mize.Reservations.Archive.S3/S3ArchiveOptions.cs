namespace Mize.Reservations.Archive.S3;

/// <summary>Settings for the S3 archive (config section <c>Archive:S3</c>). Works with real S3 and any S3-compatible server.</summary>
public sealed class S3ArchiveOptions
{
    public string Bucket { get; set; } = "reservation-archive";

    /// <summary>Optional key prefix inside the bucket (for example <c>prod/</c>); archive keys are appended to it.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>Set for an S3-compatible server (for example <c>http://minio:9000</c>); leave empty for AWS S3.</summary>
    public string? ServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";

    /// <summary>Explicit credentials; when empty the AWS default chain is used (environment, profile, instance/task role).</summary>
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }

    /// <summary>Path-style addressing (<c>host/bucket/key</c>). Defaults to true when <see cref="ServiceUrl"/> is set.</summary>
    public bool? ForcePathStyle { get; set; }

    /// <summary>Create the bucket on first write if it does not exist. Convenient for local servers; leave off for AWS.</summary>
    public bool CreateBucketIfMissing { get; set; }
}
