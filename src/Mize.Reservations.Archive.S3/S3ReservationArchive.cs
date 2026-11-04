using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Mize.Reservations.Core.Abstractions;
using Mize.Reservations.Core.Models;
using Mize.Reservations.Core.Services;

namespace Mize.Reservations.Archive.S3;

/// <summary>
/// Archive on S3 (or any S3-compatible server). A PUT that returns success is durable, which is exactly what the retention
/// pass needs before it deletes locally. Integrity is checked twice: the request carries a Content-MD5 so the server rejects
/// a corrupted body, and the returned ETag (the MD5 of a single-part, non-KMS object) is compared with our own hash.
/// Writing the same key again replaces it with identical content, so a retry after a crash is harmless.
/// </summary>
public sealed class S3ReservationArchive(IAmazonS3 s3, S3ArchiveOptions options) : IReservationArchive
{
    private int _bucketReady;

    private string Full(string key) => options.Prefix + key;

    public async Task WriteAsync(ArchiveBatch batch, CancellationToken ct = default)
    {
        ArchiveLayout.EnsureValidKey(batch.Key);
        await EnsureBucketAsync(ct);

        var bytes = ArchiveFormat.Serialize(batch.Reservations);
        var md5 = MD5.HashData(bytes);

        using var body = new MemoryStream(bytes);
        var response = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = Full(batch.Key),
            InputStream = body,
            AutoCloseStream = false,
            ContentType = "application/gzip",
            MD5Digest = Convert.ToBase64String(md5)
        }, ct);

        var etag = response.ETag?.Trim('"');
        if (etag is { Length: 32 } && etag.All(Uri.IsHexDigit)
            && !etag.Equals(Convert.ToHexString(md5), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Archive integrity check failed for '{batch.Key}': the stored object's ETag does not match the data sent.");
    }

    public async Task<IReadOnlyList<Reservation>> ReadAsync(string key, CancellationToken ct = default)
    {
        ArchiveLayout.EnsureValidKey(key);
        try
        {
            using var response = await s3.GetObjectAsync(options.Bucket, Full(key), ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return ArchiveFormat.Deserialize(buffer.ToArray());
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            throw new KeyNotFoundException($"Archive object not found: {key}", ex);
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        ArchiveLayout.EnsureValidPrefix(prefix);
        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = options.Bucket,
                Prefix = Full(prefix),
                ContinuationToken = token
            }, ct);

            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key[options.Prefix.Length..]));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);

        return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (!options.CreateBucketIfMissing || Volatile.Read(ref _bucketReady) == 1) return;

        if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, options.Bucket))
        {
            try { await s3.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, ct); }
            catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { /* another instance won the race */ }
        }
        Volatile.Write(ref _bucketReady, 1);
    }
}
