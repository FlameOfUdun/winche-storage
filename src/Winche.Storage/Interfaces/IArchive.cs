using Winche.Storage.Models;

namespace Winche.Storage.Interfaces;

public interface IArchive
{
    Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default);
    Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default);

    /// <summary>The stored object's size and ETag, or null when there's none at <paramref name="path"/>.</summary>
    Task<ArchiveObjectInfo?> HeadAsync(string path, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default);

    /// <summary>
    /// Streams every object in the archive (optionally under <paramref name="prefix"/>) for the orphan
    /// sweep to reconcile against the database. Each item carries its key and last-modified timestamp.
    /// </summary>
    IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>
    /// Starts a multipart upload of a file of <paramref name="sizeBytes"/> (an archive may cap what each part can hold
    /// by it). Calls with an upload id the archive doesn't know throw.
    /// </summary>
    Task<string> CreateMultipartUploadAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default);
    Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default);
    Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default);
    Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default);
    Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default);
}
