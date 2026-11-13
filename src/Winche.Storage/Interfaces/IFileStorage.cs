
using System.Text.Json.Nodes;
using Winche.Storage.Models;

namespace Winche.Storage.Interfaces;

public interface IFileStorage
{
    /// <summary>
    /// Registers a file pending its upload: a new one, or a replacement for the file at <paramref name="path"/> (rules:
    /// <c>create</c> when there's none, else <c>update</c>). <paramref name="precondition"/> as for a database document,
    /// e.g. <see cref="FilePrecondition.IfNotExists"/> for create-only.
    /// </summary>
    Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null,
        FilePrecondition? precondition = null, CancellationToken ct = default);
    Task<FileRecord?> GetAsync(string path, CancellationToken ct = default);
    /// <summary>Merges <paramref name="patch"/> into the file's metadata; a null value removes that key.</summary>
    Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, CancellationToken ct = default);
    /// <summary>
    /// Deletes the file at <paramref name="path"/> and every file under <c>path/</c>; rules check each one, and one
    /// refusal deletes nothing. False when there was nothing to delete.
    /// </summary>
    Task<bool> DeleteAsync(string path, CancellationToken ct = default);
    Task<UploadSession> GenerateUploadUrlAsync(string path, CancellationToken ct = default);
    Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default);
    Task<FileRecord> ConfirmUploadAsync(string path, CancellationToken ct = default);
    Task<IEnumerable<FileRecord>> ListAsync(string directory, string? mimeType = null, CancellationToken ct = default);
    Task<UploadSession> SignPartAsync(string path, int partNumber, CancellationToken ct = default);
    Task<IEnumerable<FilePart>> ListUploadedPartsAsync(string path, CancellationToken ct = default);
}
