using System.Text.Json.Nodes;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.Tests;

/// <summary>Records which inner operations the authorization decorator delegates to. Unused members throw.</summary>
internal sealed class RecordingFileStorage : IFileStorage
{
    public List<string> Calls { get; } = [];
    public FileRecord? GetReturns { get; set; }
    public bool DeleteReturns { get; set; } = true;

    public Task<FileRecord?> GetAsync(string path, CancellationToken ct = default)
    {
        Calls.Add($"Get:{path}");
        return Task.FromResult(GetReturns);
    }

    public Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        Calls.Add($"Delete:{path}");
        return Task.FromResult(DeleteReturns);
    }

    public Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<UploadSession> GenerateUploadUrlAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FileRecord> ConfirmUploadAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<FileRecord>> ListAsync(string directory, string? mimeType = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<UploadSession> SignPartAsync(string path, int partNumber, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<FilePart>> ListUploadedPartsAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
}
