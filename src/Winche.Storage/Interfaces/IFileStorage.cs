
using System.Text.Json.Nodes;
using Winche.Storage.Models;

namespace Winche.Storage.Interfaces;

public interface IFileStorage
{
    Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null, CancellationToken ct = default);
    Task<FileRecord?> GetAsync(string path, CancellationToken ct = default);
    Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, CancellationToken ct = default);
    Task<bool> DeleteAsync(string path, CancellationToken ct = default);
    Task<UploadSession> GenerateUploadUrlAsync(string path, CancellationToken ct = default);
    Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default);
    Task<FileRecord> ConfirmUploadAsync(string path, CancellationToken ct = default);
    Task<IEnumerable<FileRecord>> ListAsync(string directory, string? mimeType = null, CancellationToken ct = default);
    Task<UploadSession> SignPartAsync(string path, int partNumber, CancellationToken ct = default);
    Task<IEnumerable<FilePart>> ListUploadedPartsAsync(string path, CancellationToken ct = default);
}
