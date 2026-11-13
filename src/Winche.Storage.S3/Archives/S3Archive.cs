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
) : IArchive, Winche.Storage.Locations.IArchiveContent
{
    // ── IArchiveContent (moving files between locations) ──────────────────────────

    public async Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = this.options.BucketName, Key = path }, ct);
            return new OwnedStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task WriteAsync(string path, Stream content, string? contentType, CancellationToken ct = default)
    {
        // S3 needs the length up front: buffer streams that can't tell it to a temporary file (large ones go in parts).
        string? temp = null;
        try
        {
            if (!content.CanSeek)
            {
                temp = Path.Combine(Path.GetTempPath(), $"winche-move-{Guid.NewGuid():N}");
                await using (var file = File.Create(temp)) await content.CopyToAsync(file, ct);
            }
            using var transfer = new Amazon.S3.Transfer.TransferUtility(s3);
            var request = new Amazon.S3.Transfer.TransferUtilityUploadRequest
            {
                BucketName = this.options.BucketName, Key = path, ContentType = contentType ?? "application/octet-stream",
            };
            if (temp is not null) request.FilePath = temp; else request.InputStream = content;
            await transfer.UploadAsync(request, ct);
        }
        finally
        {
            if (temp is not null && File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>The response body, disposing the response with it.</summary>
    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); owner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private readonly S3ArchiveOptions options = options.Value;

    /// <summary>Signed links follow the service's scheme: an S3-compatible service on plain http (MinIO, LocalStack) gets http links.</summary>
    private Protocol LinkProtocol => this.options.ServiceUrl is { } u && u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS;

    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default)
    {
        var expiresAt = DateTime.UtcNow.Add(options.PresignedUrlExpiry);
        var request = new GetPreSignedUrlRequest
        {
            Protocol = LinkProtocol,
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
            Protocol = LinkProtocol,
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

    public async Task<ArchiveObjectInfo?> HeadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var meta = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = options.BucketName,
                Key = path,
            }, ct);
            return new ArchiveObjectInfo(meta.ContentLength, NormalizeETag(meta.ETag));
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

    public async Task<string> CreateMultipartUploadAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default)
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
            Protocol = LinkProtocol,
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

    public Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) =>
        Mapped(path, () => CompleteAsync(path, uploadId, ct));

    private async Task<string?> CompleteAsync(string path, string uploadId, CancellationToken ct)
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

    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default) =>
        Mapped(path, () => PartsAsync(path, uploadId, ct));

    private async Task<IEnumerable<FilePart>> PartsAsync(string path, string uploadId, CancellationToken ct)
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

    /// <summary>S3's answers about a multipart upload that can't go on, as the storage errors a client can act on.</summary>
    private static async Task<T> Mapped<T>(string path, Func<Task<T>> call)
    {
        try { return await call(); }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchUpload") { throw StorageException.UploadGone(path); }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "InvalidPart" or "InvalidPartOrder" or "EntityTooSmall")
        {
            throw new StorageException(new WincheError(WincheErrorCode.FailedPrecondition,
                $"The parts uploaded for {path} don't make a file ({ex.ErrorCode}); start the upload again.", "upload-invalid"));
        }
    }
}
