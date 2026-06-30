using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.Archives;

internal sealed class NullArchive : IArchive
{
    private static InvalidOperationException Missing() =>
        new("No IArchive has been registered. Call UseS3Archive() or register a custom IArchive implementation.");

    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) => throw Missing();
    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default) => throw Missing();
    public Task<string?> GetObjectETagAsync(string path, CancellationToken ct = default) => throw Missing();
    public Task DeleteAsync(string path, CancellationToken ct = default) => throw Missing();
    public Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default) => throw Missing();
    public IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, CancellationToken ct = default) => throw Missing();
    public Task<string> CreateMultipartUploadAsync(string path, string mimeType, CancellationToken ct = default) => throw Missing();
    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default) => throw Missing();
    public Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => throw Missing();
    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default) => throw Missing();
    public Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => throw Missing();
}
