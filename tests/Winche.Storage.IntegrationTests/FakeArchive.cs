using System.Runtime.CompilerServices;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.IntegrationTests;

/// <summary>
/// Controllable <see cref="IArchive"/> for delete/purge/upload tests: records the keys/ids handed to
/// each operation, can be told to throw to simulate an archive outage, returns configurable upload
/// metadata, and serves a fixed object listing for the orphan sweep.
/// </summary>
internal sealed class FakeArchive : IArchive
{
    // Delete / purge
    public bool ThrowOnDeleteObjects { get; set; }
    public List<string> DeletedKeys { get; } = [];
    public List<ArchivedObject> Objects { get; } = [];

    // Upload lifecycle knobs
    public string? SingleObjectETag { get; set; } = "etag-single";
    public string? MultipartETag { get; set; } = "etag-multipart";
    public string NextUploadId { get; set; } = "upload-1";

    // Recorded calls
    public List<string> AbortedUploadIds { get; } = [];
    public List<string> CompletedUploadIds { get; } = [];
    public List<string> CreatedUploadPaths { get; } = [];

    public Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        if (ThrowOnDeleteObjects) throw new InvalidOperationException("archive down");
        DeletedKeys.AddRange(paths);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(
        string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var o in Objects)
            if (string.IsNullOrEmpty(prefix) || o.Key.StartsWith(prefix, StringComparison.Ordinal))
                yield return o;
        await Task.CompletedTask;
    }

    public Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        AbortedUploadIds.Add(uploadId);
        return Task.CompletedTask;
    }

    public Task<string?> GetObjectETagAsync(string path, CancellationToken ct = default)
        => Task.FromResult(SingleObjectETag);

    public Task<string> CreateMultipartUploadAsync(string path, string mimeType, CancellationToken ct = default)
    {
        CreatedUploadPaths.Add(path);
        return Task.FromResult(NextUploadId);
    }

    public Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        CompletedUploadIds.Add(uploadId);
        return Task.FromResult(MultipartETag);
    }

    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default)
        => Task.FromResult(new UploadSession { Url = $"https://archive/put/{path}" });

    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default)
        => Task.FromResult(new UploadSession { Url = $"https://archive/part/{path}/{partNumber}" });

    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default)
        => Task.FromResult(new DownloadSession { Url = $"https://archive/get/{path}", ExpiresAt = default });

    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default)
        => Task.FromResult<IEnumerable<FilePart>>([]);

    public Task DeleteAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
}
