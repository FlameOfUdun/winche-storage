using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
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

    public async Task<bool> ObjectExistsAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = options.BucketName,
                Key = path,
            }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
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

            var response = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = options.BucketName,
                Objects = batch,
                Quiet = false,
            }, ct);

            if (response.DeleteErrors is { Count: > 0 } errs)
            {
                throw new InvalidOperationException(
                    $"S3 bulk delete failed for {errs.Count} object(s): "
                  + string.Join("; ", errs.Select(e => $"{e.Key}: {e.Code} {e.Message}")));
            }
        }
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

    public async Task CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
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

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = options.BucketName,
            Key = path,
            UploadId = uploadId,
            PartETags = [.. parts.OrderBy(p => p.PartNumber).Select(p => new PartETag(p.PartNumber ?? 1, p.ETag))],
        }, ct);
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
