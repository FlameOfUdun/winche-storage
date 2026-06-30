using Winche.Storage.Models;

namespace Winche.Storage.Interfaces;

public interface IArchive
{
    Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default);
    Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default);
    Task<string?> GetObjectETagAsync(string path, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default);

    /// <summary>
    /// Streams every object in the archive (optionally under <paramref name="prefix"/>) for the orphan
    /// sweep to reconcile against the database. Each item carries its key and last-modified timestamp.
    /// </summary>
    IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, CancellationToken ct = default);
    Task<string> CreateMultipartUploadAsync(string path, string mimeType, CancellationToken ct = default);
    Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default);
    Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default);
    Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default);
    Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default);
}
