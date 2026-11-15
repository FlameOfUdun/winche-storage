using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Rules;
using System.Text.Json.Nodes;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;
using Winche.Storage.Operations;

namespace Winche.Storage.Services;

/// <summary>
/// A check run inside a write's transaction, on each file it changes: the operation, the file before (null when new)
/// and after (null when deleted). Throwing refuses the whole write.
/// </summary>
internal delegate Task FileCheck(RuleOperation operation, FileRecord? before, FileRecord? after, CancellationToken ct);

public sealed class FileStorage : IFileStorage
{
    private readonly NpgsqlDataSource source;
    private readonly IArchive archive;
    private readonly HookInvocationDispatcher hookDispatcher;
    private readonly StorageLimits? limits;

    /// <summary>Made by <c>AddWincheStorage</c>; resolve <see cref="FileStorage"/> (unguarded) or <see cref="IFileStorage"/> (rule-checked).</summary>
    internal FileStorage(
        [FromKeyedServices(WincheStorageKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source,
        IArchive archive,
        HookInvocationDispatcher hookDispatcher,
        StorageLimits? limits = null)
    {
        this.source = source;
        this.archive = archive;
        this.hookDispatcher = hookDispatcher;
        this.limits = limits;
    }

    /// <summary>The app's before hooks (refuse or change a write), run in its transaction after any rules check.</summary>
    internal Abstraction.FileBeforeHooks? BeforeHooks { get; init; }

    public Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null,
        FilePrecondition? precondition = null, FileHeaders? headers = null, CancellationToken ct = default) =>
        SetAsync(path, mimeType, sizeBytes, metadata, precondition, headers, null, ct);

    /// <summary>
    /// Registers a file, pending its upload: a new one, or a replacement for the file already at the path (its upload
    /// starts over). <paramref name="check"/> sees the file before and after, inside the transaction, so what it allowed
    /// is what's written.
    /// </summary>
    internal async Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata,
        FilePrecondition? precondition, FileHeaders? headers, FileCheck? check, CancellationToken ct)
    {
        FilePathParser.Require(path);
        if (sizeBytes < 0 || sizeBytes > limits?.MaxFileSizeBytes)
            throw new FileTooLargeException(path, sizeBytes, limits?.MaxFileSizeBytes);

        FileRecord? before = null;
        var record = await WithHooksAsync(HookEventKind.Registered, path, async (conn, tx) =>
        {
            before = await new GetFileOperation(conn, tx).ExecuteAsync(path, ct, forUpdate: true);
            precondition?.Check(path, before);
            if (check is not null)
                await check(before is null ? RuleOperation.Create : RuleOperation.Update, before, Proposed(path, mimeType, sizeBytes, metadata, headers, before), ct);
            if (BeforeHooks is { } hooks)
            {
                var proposed = Proposed(path, mimeType, sizeBytes, metadata, headers, before);
                if (await hooks.RunAsync(path, before is null ? Abstraction.FileOperation.Create : Abstraction.FileOperation.Update, before,
                        mimeType, sizeBytes, proposed.MetaData, proposed.ContentDisposition, proposed.CacheControl, ct) is { } w)
                {
                    metadata = w.Metadata;
                    headers = new FileHeaders(w.ContentDisposition ?? "", w.CacheControl ?? "");
                }
            }
            var op = new InsertFileOperation(conn, tx);
            if (before is null)
                return await op.InsertAsync(path, mimeType, sizeBytes, metadata, headers, ct)
                    ?? throw (precondition?.Exists == false ? StorageException.AlreadyExists(path) : StorageException.Changed(path));
            return await op.ReplaceAsync(path, before.Version, mimeType, sizeBytes, metadata, headers, ct) ?? throw StorageException.Changed(path);
        }, ct);

        WincheStorageMetrics.FilesRegistered.Add(1);
        // The replaced registration's upload can never complete it now: its multipart upload and any bytes already sent
        // to its key are dropped. Confirmed bytes stay until the new upload is confirmed.
        if (before?.UploadKey is { } staleKey)
            await BestEffortAsync(async () =>
            {
                if (before.UploadId is { } stale) await archive.AbortMultipartUploadAsync(staleKey, stale, ct);
                await archive.DeleteObjectsAsync([staleKey], ct);
            });
        return record!;
    }

    /// <summary>The file a registration would leave, for checks to see before it's written.</summary>
    private static FileRecord Proposed(string path, string mimeType, long sizeBytes, JsonObject? metadata, FileHeaders? headers, FileRecord? before)
    {
        var info = FilePathParser.Parse(path);
        var now = DateTime.UtcNow;
        return new FileRecord
        {
            Id = info.Id!, Directory = info.Directory, Path = path, CreatedAt = before?.CreatedAt ?? now, UpdatedAt = now,
            MetaData = metadata ?? [], Version = (before?.Version ?? 0) + 1, MimeType = mimeType, SizeBytes = sizeBytes,
            UploadStatus = UploadStatus.Pending,
            ContentDisposition = FileHeaders.Apply(null, headers?.ContentDisposition),
            CacheControl = FileHeaders.Apply(null, headers?.CacheControl),
        };
    }

    private static async Task BestEffortAsync(Func<Task> cleanup)
    {
        try { await cleanup(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the orphan sweep reclaims what's left */ }
    }

    /// <summary>
    /// Deletes every file whose path starts with <paramref name="prefix"/> (e.g. <c>users/u1/</c>), one at a time so hooks
    /// see each delete; safe to run again. Server code: not rule-checked. Returns how many were deleted.
    /// </summary>
    public async Task<int> DeleteUnderAsync(string prefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Trim('/').Length == 0)
            throw new ArgumentException("Give a folder to delete under; deleting every file isn't allowed this way.", nameof(prefix));
        var paths = new List<string>();
        await using (var cmd = source.CreateCommand($"SELECT path FROM {Constants.WincheStorageTables.Files} WHERE starts_with(path, $1) ORDER BY path"))
        {
            cmd.Parameters.AddWithValue(prefix);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) paths.Add(reader.GetString(0));
        }
        var deleted = 0;
        foreach (var path in paths)
            if (await DeleteAsync(path, ct: ct)) deleted++;
        return deleted;
    }

    public async Task<FileRecord?> GetAsync(string path, CancellationToken ct = default)
    {
        FilePathParser.Require(path);
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new GetFileOperation(conn, null).ExecuteAsync(path, ct);
    }

    public Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, FilePrecondition? precondition = null,
        FileHeaders? headers = null, CancellationToken ct = default) =>
        UpdateMetadataAsync(path, patch, precondition, headers, null, ct);

    /// <summary>
    /// Merges <paramref name="patch"/> into the file's metadata (a null value removes the key); <paramref name="check"/>
    /// sees the file before and with the merged metadata, inside the transaction.
    /// </summary>
    internal Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, FilePrecondition? precondition,
        FileHeaders? headers, FileCheck? check, CancellationToken ct)
    {
        FilePathParser.Require(path);
        return WithHooksAsync(HookEventKind.MetadataUpdated, path, async (conn, tx) =>
        {
            var before = await new GetFileOperation(conn, tx).ExecuteAsync(path, ct, forUpdate: true);
            if (before is null)
            {
                if (check is not null) await check(RuleOperation.Update, null, null, ct);
                precondition?.Check(path, null);
                return null;
            }
            precondition?.Check(path, before);
            var after = before with
            {
                MetaData = UpdateMetadataOperation.Merge(before.MetaData, patch),
                ContentDisposition = FileHeaders.Apply(before.ContentDisposition, headers?.ContentDisposition),
                CacheControl = FileHeaders.Apply(before.CacheControl, headers?.CacheControl),
                Version = before.Version + 1,
                UpdatedAt = DateTime.UtcNow,
            };
            if (check is not null) await check(RuleOperation.Update, before, after, ct);
            if (BeforeHooks is { } hooks && await hooks.RunAsync(path, Abstraction.FileOperation.Update, before, before.MimeType, before.SizeBytes,
                    after.MetaData, after.ContentDisposition, after.CacheControl, ct) is { } w)
                after = after with { MetaData = w.Metadata!, ContentDisposition = w.ContentDisposition, CacheControl = w.CacheControl };
            return await new UpdateMetadataOperation(conn, tx).ExecuteAsync(path, before.Version, after.MetaData, after.ContentDisposition, after.CacheControl, ct)
                ?? throw StorageException.Changed(path);
        }, ct);
    }

    public Task<bool> DeleteAsync(string path, FilePrecondition? precondition = null, CancellationToken ct = default) =>
        DeleteAsync(path, precondition, null, ct);

    /// <summary>
    /// Deletes the file at <paramref name="path"/> and every file under <c>path/</c>, like a database document with its
    /// subcollections. <paramref name="check"/> sees each file, inside the transaction: one refusal and nothing is deleted.
    /// </summary>
    internal async Task<bool> DeleteAsync(string path, FilePrecondition? precondition, FileCheck? check, CancellationToken ct)
    {
        FilePathParser.Require(path);
        // The database is the source of truth: commit the row removal FIRST, then clean up the
        // archive best-effort. An archive failure must never roll the delete back — that would leave
        // an undeletable row (object claimed present, every retry re-failing). Any object we miss is
        // a harmless orphan reclaimed by the orphan sweep.
        IReadOnlyList<FileRecord> files;
        IReadOnlyList<string> deleted;

        await using (var conn = await source.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var op = new DeleteFileOperation(conn, tx);
            files = await op.SelectForUpdateAsync(path, ct);
            precondition?.Check(path, files.FirstOrDefault(f => f.Path == path));
            if (check is not null)
            {
                if (files.Count == 0) await check(RuleOperation.Delete, null, null, ct);
                foreach (var file in files) await check(RuleOperation.Delete, file, null, ct);
            }
            if (BeforeHooks is { } hooks)
                foreach (var file in files)
                    await hooks.RunAsync(file.Path, Abstraction.FileOperation.Delete, file, null, null, null, null, null, ct);
            if (files.Count == 0) return false;
            deleted = await op.ExecuteAsync([.. files.Select(f => f.Path)], ct);
            files = [.. files.Where(f => deleted.Contains(f.Path))];
            foreach (var p in deleted)
                await StorageFeed.AppendAsync(conn, tx, HookEventKind.Deleted, p, null, ct);
            await tx.CommitAsync(ct);
        }

        if (deleted.Count == 0) return false;

        // Logical delete (and its feed entries for hooks) is durable now, whatever happens to the archive cleanup.
        hookDispatcher.Wake();
        WincheStorageMetrics.FilesDeleted.Add(deleted.Count);

        // Post-commit archive cleanup — best-effort, never throws back to the caller.
        await BestEffortAsync(async () =>
        {
            foreach (var file in files)
                if (file is { UploadKey: { } key, UploadId: { } uploadId })
                    await archive.AbortMultipartUploadAsync(key, uploadId, ct);
            // Each key belongs to this file alone, so nothing re-created at the path meanwhile can be hit.
            await archive.DeleteObjectsAsync([.. files.SelectMany(f => new[] { f.ObjectKey, f.UploadKey }).OfType<string>()], ct);
        });
        return true;
    }

    public async Task<UploadSession> GenerateUploadUrlAsync(string path, CancellationToken ct = default)
    {
        var file = await GetAsync(path, ct)
            ?? throw new FileRecordNotFoundException(path);

        if (file.UploadStatus != UploadStatus.Pending)
            throw new InvalidUploadStatusException(path, UploadStatus.Pending, file.UploadStatus);

        if (file.UploadId is { } multipart)
        {
            await archive.AbortMultipartUploadAsync(UploadKeyOf(file), multipart, ct);
            await using var conn = await source.OpenConnectionAsync(ct);
            await new SetUploadIdOperation(conn, null).ClearAsync(path, multipart, ct);
        }

        var session = await archive.GenerateUploadUrlAsync(UploadKeyOf(file), file.MimeType, file.SizeBytes, ct);
        WincheStorageMetrics.LinksSigned.Add(1, new KeyValuePair<string, object?>("kind", "upload"));
        hookDispatcher.Enqueue(path, (h, t) => h.OnUploadUrlGeneratedAsync(path, session, t));
        return session;
    }

    public async Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default)
    {
        var file = await GetAsync(path, ct)
            ?? throw new FileRecordNotFoundException(path);

        if (file.UploadStatus != UploadStatus.Complete)
            throw new InvalidUploadStatusException(path, UploadStatus.Complete, file.UploadStatus);

        var session = await archive.GenerateDownloadUrlAsync(file.ObjectKey ?? file.Path,
            new DownloadHeaders(file.MimeType, file.ContentDisposition, file.CacheControl), ct);
        WincheStorageMetrics.LinksSigned.Add(1, new KeyValuePair<string, object?>("kind", "download"));
        hookDispatcher.Enqueue(path, (h, t) => h.OnDownloadUrlGeneratedAsync(path, session, t));
        return session;
    }

    /// <summary>
    /// Marks the file's upload complete once the archive holds exactly the declared bytes. Only the file that was
    /// checked is confirmed (still pending, same version): a confirm racing a replacement fails with <c>aborted</c>.
    /// Confirming a file that's already complete returns it, so a retry is harmless.
    /// </summary>
    public async Task<FileRecord> ConfirmUploadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var confirmed = await ConfirmAsync(path, ct);
            WincheStorageMetrics.UploadsConfirmed.Add(1);
            WincheStorageMetrics.UploadBytes.Add(confirmed.SizeBytes);
            return confirmed;
        }
        catch (Exception ex) when (ex is FileNotUploadedException or UploadSizeMismatchException or StorageException)
        {
            WincheStorageMetrics.UploadsRefused.Add(1);
            throw;
        }
    }

    private async Task<FileRecord> ConfirmAsync(string path, CancellationToken ct)
    {
        var file = await GetAsync(path, ct) ?? throw new FileRecordNotFoundException(path);
        if (file.UploadStatus == UploadStatus.Complete) return file;
        if (file.UploadStatus != UploadStatus.Pending)
            throw new InvalidUploadStatusException(path, UploadStatus.Pending, file.UploadStatus);

        var key = UploadKeyOf(file);
        string? contentHash;
        if (file.UploadId is { } uploadId)
        {
            // Parts are signed without a length, so this is the only point where the declared size (and with
            // it MaxFileSizeBytes) can be held to. A mismatch discards the parts; the client starts over.
            var uploaded = (await archive.ListPartsAsync(key, uploadId, ct)).Sum(p => p.Size ?? 0);
            if (uploaded != file.SizeBytes)
            {
                await archive.AbortMultipartUploadAsync(key, uploadId, ct);
                await using var abortConn = await source.OpenConnectionAsync(ct);
                await new SetUploadIdOperation(abortConn, null).ClearAsync(path, uploadId, ct);
                throw new UploadSizeMismatchException(path, file.SizeBytes, uploaded);
            }
            contentHash = await archive.CompleteMultipartUploadAsync(key, uploadId, ct);
        }
        else
        {
            // A single upload link can't promise the length on every archive, so the stored object is measured.
            var stored = await archive.HeadAsync(key, ct) ?? throw new FileNotUploadedException(path);
            if (stored.Size != file.SizeBytes) throw new UploadSizeMismatchException(path, file.SizeBytes, stored.Size);
            contentHash = stored.ETag;
        }

        var confirmed = await WithHooksAsync(HookEventKind.UploadConfirmed, path,
            (conn, tx) => new ConfirmUploadOperation(conn, tx).ExecuteAsync(path, file.Version, contentHash, ct), ct)
            ?? throw StorageException.Changed(path);

        // The bytes this upload replaced aren't read any more.
        if (file.ObjectKey is { } replaced && replaced != confirmed.ObjectKey)
            await BestEffortAsync(() => archive.DeleteObjectsAsync([replaced], ct));
        return confirmed;
    }

    /// <summary>Where a pending file's upload goes (its path, for one registered before keys of their own).</summary>
    private static string UploadKeyOf(FileRecord file) => file.UploadKey ?? file.Path;

    /// <summary>
    /// Runs a file change and records it in storage's feed in one transaction, so a committed change always reaches its
    /// hooks (<see cref="StorageHookService"/>), even across a restart. A null result (no such file) delivers nothing.
    /// </summary>
    private async Task<FileRecord?> WithHooksAsync(string kind, string path,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<FileRecord?>> change, CancellationToken ct)
    {
        FileRecord? record;
        await using (var conn = await source.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            record = await change(conn, tx);
            if (record is not null)
                await StorageFeed.AppendAsync(conn, tx, kind, path, record, ct);
            await tx.CommitAsync(ct);
        }
        if (record is not null) hookDispatcher.Wake();
        return record;
    }

    /// <summary>
    /// Server code: stores <paramref name="content"/> (<paramref name="sizeBytes"/> long) as the file at
    /// <paramref name="path"/>, new or replacing what's there, without an upload link. Not rule-checked, like the rest
    /// of this class; hooks and changes fire as for an upload.
    /// </summary>
    public async Task<FileRecord> WriteAsync(string path, Stream content, string mimeType, long sizeBytes,
        JsonObject? metadata = null, FileHeaders? headers = null, CancellationToken ct = default)
    {
        if (archive is not Locations.IArchiveContent bytes)
            throw new NotSupportedException("This archive can't take bytes from the server.");
        var file = await SetAsync(path, mimeType, sizeBytes, metadata, null, headers, ct);
        await bytes.WriteAsync(UploadKeyOf(file), content, mimeType, ct);
        return await ConfirmUploadAsync(path, ct);
    }

    /// <summary>Server code: the bytes of the file at <paramref name="path"/>, or null when there's no uploaded file there.</summary>
    public async Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        if (archive is not Locations.IArchiveContent bytes)
            throw new NotSupportedException("This archive can't hand bytes to the server.");
        var file = await GetAsync(path, ct);
        return file is { UploadStatus: UploadStatus.Complete } ? await bytes.OpenReadAsync(file.ObjectKey ?? file.Path, ct) : null;
    }

    /// <summary>The most a list page holds, and its default size (as Firebase).</summary>
    public const int MaxListResults = 1000;

    public async Task<FileListResult> ListAsync(string directory, FileListOptions? options = null, CancellationToken ct = default)
    {
        if (directory.Length > 0) FilePathParser.Require(directory);
        var max = Math.Clamp(options?.MaxResults ?? MaxListResults, 1, MaxListResults);
        var after = options?.PageToken is { } token ? DirectoryPageToken.Decode(token) : null;

        await using var conn = await source.OpenConnectionAsync(ct);
        // One extra of each says whether there's more; files and folders are merged by name, as one listing.
        var folders = await new ListDirectoryIdsOperation(conn, null).ListAsync(directory, after, max + 1, ct);
        var files = await new ListFilesOperation(conn, null).ExecuteAsync(directory, options?.MimeType, after, max + 1, ct);
        var names = folders.Concat(files.Select(f => f.Id)).Distinct().Order(StringComparer.Ordinal).ToList();
        var more = names.Count > max;
        var last = more ? names[max - 1] : null;
        bool InPage(string name) => last is null || string.CompareOrdinal(name, last) <= 0;

        var prefix = directory.Length == 0 ? "" : directory + "/";
        return new FileListResult(
            [.. files.Where(f => InPage(f.Id))],
            [.. folders.Where(InPage).Select(f => prefix + f)],
            last is null ? null : DirectoryPageToken.Encode(last));
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

        // Multipart uploads nobody finished, for archives that keep them (best-effort, like the rest of the sweep).
        if (archive is IAbandonedUploads uploads)
            await BestEffortAsync(async () => purged += await uploads.DropAbandonedUploadsAsync(cutoffUtc, ct));

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
        var link = await SignAsync(path, partNumber, ct);
        WincheStorageMetrics.LinksSigned.Add(1, new KeyValuePair<string, object?>("kind", "part"));
        return link;
    }

    private async Task<UploadSession> SignAsync(string path, int partNumber, CancellationToken ct)
    {
        var file = await GetAsync(path, ct)
            ?? throw new FileRecordNotFoundException(path);

        if (file.UploadStatus != UploadStatus.Pending)
            throw new InvalidUploadStatusException(path, UploadStatus.Pending, file.UploadStatus);

        var key = UploadKeyOf(file);
        if (file.UploadId is { } existing)
            return await archive.SignPartAsync(key, existing, partNumber, ct);
        if (partNumber != 1)
            throw StorageException.NoUpload(path);

        // Two requests may start an upload at once: one records its id, the other gives its own up and uses the winner's.
        var started = await archive.CreateMultipartUploadAsync(key, file.MimeType, file.SizeBytes, ct);
        await using (var conn = await source.OpenConnectionAsync(ct))
            if (await new SetUploadIdOperation(conn, null).StartAsync(path, file.Version, started, ct))
                return await archive.SignPartAsync(key, started, partNumber, ct);

        await BestEffortAsync(() => archive.AbortMultipartUploadAsync(key, started, ct));
        var now = await GetAsync(path, ct);
        if (now is { UploadStatus: UploadStatus.Pending, UploadId: { } winner } && now.Version == file.Version)
            return await archive.SignPartAsync(key, winner, partNumber, ct);
        throw StorageException.Changed(path);
    }

    public async Task<IEnumerable<FilePart>> ListUploadedPartsAsync(string path, CancellationToken ct = default)
    {
        var file = await GetAsync(path, ct)
            ?? throw new FileRecordNotFoundException(path);

        if (file.UploadId is null)
            return [];

        return await archive.ListPartsAsync(UploadKeyOf(file), file.UploadId, ct);
    }
}
