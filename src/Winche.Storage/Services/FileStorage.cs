using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.Json.Nodes;
using Winche.Storage.Constants;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;
using Winche.Storage.Operations;

namespace Winche.Storage.Services;

public sealed class FileStorage(
    [FromKeyedServices(ServiceKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source,
    IArchive archive,
    HookInvocationDispatcher hookDispatcher
) : IFileStorage
{
    public Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null, CancellationToken ct = default) =>
        SetUnprotectedAsync(path, mimeType, sizeBytes, metadata, ct);

    public Task<FileRecord?> GetAsync(string path, CancellationToken ct = default) =>
        GetUnprotectedAsync(path, ct);

    public Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, CancellationToken ct = default) =>
        UpdateUnprotectedAsync(path, patch, ct);

    public Task<bool> DeleteAsync(string path, CancellationToken ct = default) =>
        DeleteUnprotectedAsync(path, ct);

    public Task<UploadSession> GenerateUploadUrlAsync(string path, CancellationToken ct = default) =>
        GenerateUploadUrlUnprotectedAsync(path, ct);

    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default) =>
        GenerateDownloadUrlUnprotectedAsync(path, ct);

    public Task<FileRecord> ConfirmUploadAsync(string path, CancellationToken ct = default) =>
        ConfirmUploadUnprotectedAsync(path, ct);

    public Task<IEnumerable<FileRecord>> ListAsync(string directory, string? mimeType = null, CancellationToken ct = default) =>
        ListUnprotectedAsync(directory, mimeType, ct);

    public async Task<UploadSession> SignPartAsync(string path, int partNumber, CancellationToken ct = default)
    {
        var file = await GetUnprotectedAsync(path, ct)
            ?? throw new FileRecordNotFoundException(path);

        if (file.UploadStatus != UploadStatus.Pending)
            throw new InvalidUploadStatusException(path, UploadStatus.Pending, file.UploadStatus);

        if (file.UploadId is null)
        {
            if (partNumber != 1)
                throw new InvalidOperationException($"No active multipart upload for '{path}'. Start from part 1.");

            var uploadId = await archive.CreateMultipartUploadAsync(path, file.MimeType, ct);
            await using var conn = await source.OpenConnectionAsync(ct);
            await new SetUploadIdOperation(conn, null).ExecuteAsync(path, uploadId, ct);
            return await archive.SignPartAsync(path, uploadId, partNumber, ct);
        }

        return await archive.SignPartAsync(path, file.UploadId, partNumber, ct);
    }

    public async Task<IEnumerable<FilePart>> ListUploadedPartsAsync(string path, CancellationToken ct = default)
    {
        var file = await GetUnprotectedAsync(path, ct)
            ?? throw new FileRecordNotFoundException(path);

        if (file.UploadId is null)
            return [];

        return await archive.ListPartsAsync(path, file.UploadId, ct);
    }

    public async Task<FileRecord> SetUnprotectedAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        var record = await new InsertFileOperation(conn, null).ExecuteAsync(path, mimeType, sizeBytes, metadata, ct);
        hookDispatcher.Enqueue(path, (h, t) => h.OnFileRegisteredAsync(record, t));
        return record;
    }

    public async Task<FileRecord?> GetUnprotectedAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new GetFileOperation(conn, null).ExecuteAsync(path, ct);
    }

    public async Task<FileRecord?> UpdateUnprotectedAsync(string path, JsonObject patch, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        var record = await new UpdateMetadataOperation(conn, null).ExecuteAsync(path, patch, ct);
        if (record is not null) hookDispatcher.Enqueue(path, (h, t) => h.OnMetadataUpdatedAsync(record, t));
        return record;
    }

    public async Task<bool> DeleteUnprotectedAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var op = new DeleteFileOperation(conn, tx);

            var candidates = await op.SelectForUpdateAsync(path, ct);
            var deleted = await op.ExecuteAsync(path, ct);

            foreach (var candidate in candidates)
            {
                if (candidate.UploadStatus == UploadStatus.Pending && candidate.UploadId is not null)
                    await archive.AbortMultipartUploadAsync(candidate.Path, candidate.UploadId, ct);
            }

            if (deleted.Count > 0)
                await archive.DeleteObjectsAsync(deleted, ct);

            await tx.CommitAsync(ct);

            foreach (var p in deleted)
                hookDispatcher.Enqueue(p, (h, t) => h.OnFileDeletedAsync(p, t));

            return deleted.Count > 0;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    public async Task<UploadSession> GenerateUploadUrlUnprotectedAsync(string path, CancellationToken ct = default)
    {
        var file = await GetUnprotectedAsync(path, ct)
            ?? throw new FileRecordNotFoundException(path);

        if (file.UploadStatus != UploadStatus.Pending)
            throw new InvalidUploadStatusException(path, UploadStatus.Pending, file.UploadStatus);

        if (file.UploadId is not null)
        {
            await archive.AbortMultipartUploadAsync(path, file.UploadId, ct);
            await using var conn = await source.OpenConnectionAsync(ct);
            await new SetUploadIdOperation(conn, null).ExecuteAsync(path, null, ct);
        }

        var session = await archive.GenerateUploadUrlAsync(file.Path, file.MimeType, file.SizeBytes, ct);
        hookDispatcher.Enqueue(path, (h, t) => h.OnUploadUrlGeneratedAsync(path, session, t));
        return session;
    }

    public async Task<DownloadSession> GenerateDownloadUrlUnprotectedAsync(string path, CancellationToken ct = default)
    {
        var file = await GetUnprotectedAsync(path, ct)
            ?? throw new FileRecordNotFoundException("File not found");

        if (file.UploadStatus != UploadStatus.Complete)
            throw new InvalidUploadStatusException(path, UploadStatus.Complete, file.UploadStatus);

        var session = await archive.GenerateDownloadUrlAsync(file.Path, ct);
        hookDispatcher.Enqueue(path, (h, t) => h.OnDownloadUrlGeneratedAsync(path, session, t));
        return session;
    }

    public async Task<FileRecord> ConfirmUploadUnprotectedAsync(string path, CancellationToken ct = default)
    {
        var file = await GetUnprotectedAsync(path, ct)
            ?? throw new FileRecordNotFoundException("File not found");

        if (file.UploadStatus != UploadStatus.Pending)
            throw new InvalidUploadStatusException(path, UploadStatus.Pending, file.UploadStatus);

        if (file.UploadId is not null)
        {
            await archive.CompleteMultipartUploadAsync(path, file.UploadId, ct);
            await using var conn1 = await source.OpenConnectionAsync(ct);
            await new SetUploadIdOperation(conn1, null).ExecuteAsync(path, null, ct);
        }
        else
        {
            var exists = await archive.ObjectExistsAsync(path, ct);
            if (!exists)
                throw new FileNotUploadedException(path);
        }

        await using var conn = await source.OpenConnectionAsync(ct);
        var record = await new ConfirmUploadOperation(conn, null).ExecuteAsync(path, ct)
            ?? throw new FileRecordNotFoundException(path);
        hookDispatcher.Enqueue(path, (h, t) => h.OnUploadConfirmedAsync(record, t));
        return record;
    }

    public async Task<IEnumerable<FileRecord>> ListUnprotectedAsync(string directory, string? mimeType = null, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new ListFilesOperation(conn, null).ExecuteAsync(directory, mimeType, ct);
    }
}
