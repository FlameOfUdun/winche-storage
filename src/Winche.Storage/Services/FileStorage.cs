using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.Json.Nodes;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
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
    public async Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        var record = await new InsertFileOperation(conn, null).ExecuteAsync(path, mimeType, sizeBytes, metadata, ct);
        hookDispatcher.Enqueue(path, (h, t) => h.OnFileRegisteredAsync(record, t));
        return record;
    }

    public async Task<FileRecord?> GetAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new GetFileOperation(conn, null).ExecuteAsync(path, ct);
    }

    public async Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        var record = await new UpdateMetadataOperation(conn, null).ExecuteAsync(path, patch, ct);
        if (record is not null) hookDispatcher.Enqueue(path, (h, t) => h.OnMetadataUpdatedAsync(record, t));
        return record;
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        // The database is the source of truth: commit the row removal FIRST, then clean up the
        // archive best-effort. An archive failure must never roll the delete back — that would leave
        // an undeletable row (object claimed present, every retry re-failing). Any object we miss is
        // a harmless orphan reclaimed by the orphan sweep.
        IReadOnlyList<FileDeleteCandidate> candidates;
        IReadOnlyList<string> deleted;

        await using (var conn = await source.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            try
            {
                var op = new DeleteFileOperation(conn, tx);
                candidates = await op.SelectForUpdateAsync(path, ct);
                deleted = await op.ExecuteAsync(path, ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                try { await tx.RollbackAsync(ct); } catch { }
                throw;
            }
        }

        if (deleted.Count == 0)
            return false;

        // Logical delete is durable now; surface it to hooks regardless of archive cleanup outcome.
        foreach (var p in deleted)
            hookDispatcher.Enqueue(p, (h, t) => h.OnFileDeletedAsync(p, t));

        // Post-commit archive cleanup — best-effort, never throws back to the caller.
        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate.UploadStatus == UploadStatus.Pending && candidate.UploadId is not null)
                    await archive.AbortMultipartUploadAsync(candidate.Path, candidate.UploadId, ct);
            }

            await archive.DeleteObjectsAsync(deleted, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed by design: the orphan sweep reclaims anything left in the archive.
        }

        return true;
    }

    public async Task<UploadSession> GenerateUploadUrlAsync(string path, CancellationToken ct = default)
    {
        var file = await GetAsync(path, ct)
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

    public async Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default)
    {
        var file = await GetAsync(path, ct)
            ?? throw new FileRecordNotFoundException("File not found");

        if (file.UploadStatus != UploadStatus.Complete)
            throw new InvalidUploadStatusException(path, UploadStatus.Complete, file.UploadStatus);

        var session = await archive.GenerateDownloadUrlAsync(file.Path, ct);
        hookDispatcher.Enqueue(path, (h, t) => h.OnDownloadUrlGeneratedAsync(path, session, t));
        return session;
    }

    public async Task<FileRecord> ConfirmUploadAsync(string path, CancellationToken ct = default)
    {
        var file = await GetAsync(path, ct) ?? throw new FileRecordNotFoundException("File not found");
        if (file.UploadStatus != UploadStatus.Pending)
            throw new InvalidUploadStatusException(path, UploadStatus.Pending, file.UploadStatus);

        string? contentHash;
        if (file.UploadId is not null)
        {
            contentHash = await archive.CompleteMultipartUploadAsync(path, file.UploadId, ct);
            await using var conn1 = await source.OpenConnectionAsync(ct);
            await new SetUploadIdOperation(conn1, null).ExecuteAsync(path, null, ct);
        }
        else
        {
            contentHash = await archive.GetObjectETagAsync(path, ct);
            if (contentHash is null) throw new FileNotUploadedException(path);
        }

        await using var conn = await source.OpenConnectionAsync(ct);
        var record = await new ConfirmUploadOperation(conn, null).ExecuteAsync(path, contentHash, ct)
            ?? throw new FileRecordNotFoundException(path);
        hookDispatcher.Enqueue(path, (h, t) => h.OnUploadConfirmedAsync(record, t));
        return record;
    }

    public async Task<IEnumerable<FileRecord>> ListAsync(string directory, string? mimeType = null, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new ListFilesOperation(conn, null).ExecuteAsync(directory, mimeType, ct);
    }

    /// <summary>
    /// Lists the immediate sub-directory names directly under <paramref name="parentDirectory"/>
    /// (or the top-level directories when null/empty). <b>Privileged</b>: this method is exposed
    /// only on the concrete <see cref="FileStorage"/> class, never through <c>IFileStorage</c>,
    /// and is never evaluated by the rules engine — mirroring Firestore's Admin-SDK-only
    /// <c>listCollectionIds</c> which security rules never intercept.
    /// Directory names are distinct and ordered by UTF-8 byte order (COLLATE "C").
    /// </summary>
    public async Task<ListDirectoryIdsResult> ListDirectoryIdsAsync(
        string? parentDirectory, int? pageSize = null, string? pageToken = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(parentDirectory)
            && !FilePathParser.IsValidPath(parentDirectory, out var error))
            throw new ArgumentException(error);

        var size = NormalizePageSize(pageSize);
        var after = pageToken is null ? null : DirectoryPageToken.Decode(pageToken);

        await using var conn = await source.OpenConnectionAsync(ct);
        // Fetch one extra row to detect whether another page exists.
        var ids = await new ListDirectoryIdsOperation(conn, null).ListAsync(parentDirectory, after, size + 1, ct);

        if (ids.Count <= size)
            return new ListDirectoryIdsResult(ids, null);

        var page = ids.Take(size).ToList();
        return new ListDirectoryIdsResult(page, DirectoryPageToken.Encode(page[^1]));
    }

    /// <summary>
    /// PRIVILEGED. Reconciles the archive against the database: deletes every archive object that has
    /// no matching <c>winche_files</c> row <em>and</em> is older than <paramref name="graceWindow"/>.
    /// The grace window protects in-flight uploads (object PUT but row not yet committed) from being
    /// reaped. Like <see cref="ListDirectoryIdsAsync"/> this is exposed only on the concrete
    /// <see cref="FileStorage"/>, never through <c>IFileStorage</c>, and is never evaluated by the
    /// rules engine — it is an admin/server-side sweep. Archive deletes are best-effort. Returns the
    /// number of objects identified as orphans and submitted for deletion.
    /// </summary>
    public async Task<int> PurgeOrphansAsync(string? prefix, TimeSpan graceWindow, CancellationToken ct = default)
    {
        var cutoffUtc = DateTime.UtcNow - graceWindow;
        const int batchSize = 1000;

        var purged = 0;
        var batch = new List<ArchivedObject>(batchSize);

        await foreach (var obj in archive.ListObjectsAsync(prefix, ct))
        {
            // Inside the grace window it cannot be an orphan yet — skip before the DB round-trip.
            if (obj.LastModifiedUtc >= cutoffUtc) continue;

            batch.Add(obj);
            if (batch.Count >= batchSize)
                purged += await PurgeBatchAsync(batch, cutoffUtc, ct);
        }
        if (batch.Count > 0)
            purged += await PurgeBatchAsync(batch, cutoffUtc, ct);

        return purged;
    }

    private async Task<int> PurgeBatchAsync(List<ArchivedObject> batch, DateTime cutoffUtc, CancellationToken ct)
    {
        var keys = batch.Select(o => o.Key).ToArray();

        await using var conn = await source.OpenConnectionAsync(ct);
        var existing = await new SelectExistingPathsOperation(conn, null).ExecuteAsync(keys, ct);

        var orphans = OrphanSelector.SelectOrphans(batch, existing, cutoffUtc);
        if (orphans.Count > 0)
            await archive.DeleteObjectsAsync(orphans, ct); // best-effort; never throws

        batch.Clear();
        return orphans.Count;
    }

    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 300;

    private static int NormalizePageSize(int? pageSize) =>
        pageSize is null or <= 0 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);

    public async Task<UploadSession> SignPartAsync(string path, int partNumber, CancellationToken ct = default)
    {
        var file = await GetAsync(path, ct)
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
        var file = await GetAsync(path, ct)
            ?? throw new FileRecordNotFoundException(path);

        if (file.UploadId is null)
            return [];

        return await archive.ListPartsAsync(path, file.UploadId, ct);
    }
}
