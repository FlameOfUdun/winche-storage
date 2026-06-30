using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;
using Winche.Storage.S3.Models;

namespace Winche.Storage.S3.Archives;

public sealed class S3Archive(
    IAmazonS3 s3,
    IOptions<S3ArchiveOptions> options
) : IArchive
{
    private readonly S3ArchiveOptions options = options.Value;

    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default)
    {
        var expiresAt = DateTime.UtcNow.Add(options.PresignedUrlExpiry);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = options.BucketName,
            Key = path,
            Verb = HttpVerb.PUT,
            Expires = expiresAt,
            ContentType = mimeType,
        };
        request.Headers["Content-Length"] = sizeBytes.ToString();

        return Task.FromResult(new UploadSession
        {
            Url = s3.GetPreSignedURL(request),
            ExpiresAt = expiresAt,
        });
    }

    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default)
    {
        var expiresAt = DateTime.UtcNow.Add(options.PresignedUrlExpiry);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = options.BucketName,
            Key = path,
            Verb = HttpVerb.GET,
            Expires = expiresAt,
        };

        return Task.FromResult(new DownloadSession
        {
            Url = s3.GetPreSignedURL(request),
            ExpiresAt = expiresAt,
        });
    }

    private static string? NormalizeETag(string? etag) => etag?.Trim('"');

    public async Task<string?> GetObjectETagAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var meta = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = options.BucketName,
                Key = path,
            }, ct);
            return NormalizeETag(meta.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = options.BucketName,
            Key = path,
        }, ct);
    }

    /// <summary>
    /// Best-effort bulk delete: never throws for S3-side failures (per-key <c>DeleteErrors</c> or
    /// transport faults) and keeps processing the remaining batches. The database is the source of
    /// truth, so anything left behind here is a harmless orphan that the orphan sweep reclaims later.
    /// Only cancellation propagates.
    /// </summary>
    public async Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var all = paths as IReadOnlyList<string> ?? paths.ToList();
        if (all.Count == 0) return;

        const int batchSize = 1000;
        for (int i = 0; i < all.Count; i += batchSize)
        {
            var batch = all
                .Skip(i)
                .Take(batchSize)
                .Select(p => new KeyVersion { Key = p })
                .ToList();

            try
            {
                await s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = options.BucketName,
                    Objects = batch,
                    Quiet = true,
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort: swallow and continue. The orphan sweep is the durability backstop.
            }
        }
    }

    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(
        string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? continuationToken = null;
        do
        {
            var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = options.BucketName,
                Prefix = string.IsNullOrEmpty(prefix) ? null : prefix,
                ContinuationToken = continuationToken,
            }, ct);

            foreach (var obj in response.S3Objects ?? [])
            {
                var modified = DateTime.SpecifyKind(obj.LastModified ?? DateTime.MinValue, DateTimeKind.Utc);
                yield return new ArchivedObject(obj.Key, modified);
            }

            continuationToken = (response.IsTruncated ?? false) ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }

    public async Task<string> CreateMultipartUploadAsync(string path, string mimeType, CancellationToken ct = default)
    {
        var response = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = options.BucketName,
            Key = path,
            ContentType = mimeType,
        }, ct);
        return response.UploadId;
    }

    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default)
    {
        var expiresAt = DateTime.UtcNow.Add(options.PresignedUrlExpiry);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = options.BucketName,
            Key = path,
            Verb = HttpVerb.PUT,
            Expires = expiresAt,
            PartNumber = partNumber,
            UploadId = uploadId,
        };

        return Task.FromResult(new UploadSession
        {
            Url = s3.GetPreSignedURL(request),
            ExpiresAt = expiresAt,
        });
    }

    public async Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        var parts = new List<PartDetail>();
        string? marker = null;
        ListPartsResponse listResponse;
        do
        {
            listResponse = await s3.ListPartsAsync(new ListPartsRequest
            {
                BucketName = options.BucketName,
                Key = path,
                UploadId = uploadId,
                PartNumberMarker = marker,
            }, ct);
            parts.AddRange(listResponse.Parts ?? []);
            marker = listResponse.NextPartNumberMarker?.ToString();
        } while (listResponse.IsTruncated ?? false);

        var resp = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = options.BucketName,
            Key = path,
            UploadId = uploadId,
            PartETags = [.. parts.OrderBy(p => p.PartNumber).Select(p => new PartETag(p.PartNumber ?? 1, p.ETag))],
        }, ct);
        // Persisted as the content fingerprint for staleness checks: any overwrite
        // changes the ETag. A multipart ETag is composite ("<md5>-<parts>"), not a
        // plain content hash — sufficient for change detection, not byte-identity.
        return NormalizeETag(resp.ETag);
    }

    public async Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default)
    {
        var parts = new List<PartDetail>();
        string? marker = null;
        ListPartsResponse listResponse;
        do
        {
            listResponse = await s3.ListPartsAsync(new ListPartsRequest
            {
                BucketName = options.BucketName,
                Key = path,
                UploadId = uploadId,
                PartNumberMarker = marker,
            }, ct);
            parts.AddRange(listResponse.Parts ?? []);
            marker = listResponse.NextPartNumberMarker?.ToString();
        } while (listResponse.IsTruncated ?? false);

        return parts.Select(p => new FilePart
        {
            Number = p.PartNumber ?? 1,
            Size = p.Size,
        });
    }

    public async Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        try
        {
            await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = options.BucketName,
                Key = path,
                UploadId = uploadId,
            }, ct);
        }
        catch (AmazonS3Exception ex) when (string.Equals(ex.ErrorCode, "NoSuchUpload", StringComparison.Ordinal))
        {
            // Already aborted or completed — idempotent.
        }
    }
}
