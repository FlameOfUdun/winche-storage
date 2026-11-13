using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Winche.Core;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.Locations;

/// <summary>
/// The archive FileStorage uses when the location can change: new files go to the location in use; during a move,
/// deletes reach both places, and after the switch reads fall back to the old location for files not copied yet.
/// </summary>
internal sealed class ArchiveRouter(SettingsArea<StorageLocationSettings> area, ArchiveLocations locations) : IArchive, IAbandonedUploads, IArchiveContent
{
    // Multipart uploads begun before a switch finish where they began.
    private readonly ConcurrentDictionary<string, IArchive> _uploads = new();

    private StorageLocationSettings State => area.Current;
    private Task<IArchive> ActiveAsync(CancellationToken ct) => locations.GetAsync(State.Active, ct);

    /// <summary>The other place during a move: its target while copying (deletes), its source after the switch (deletes and reads).</summary>
    private async Task<IArchive?> OtherAsync(CancellationToken ct) => State.Move switch
    {
        { Phase: StorageMove.Switched } m => await locations.GetAsync(m.From, ct),
        { To: { } to } => await locations.GetAsync(to, ct),
        _ => null,
    };

    private bool Switched => State.Move is { Phase: StorageMove.Switched };

    public async Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) =>
        await (await ActiveAsync(ct)).GenerateUploadUrlAsync(path, mimeType, sizeBytes, ct);

    public async Task<DownloadSession> GenerateDownloadUrlAsync(string path, DownloadHeaders? headers = null, CancellationToken ct = default)
    {
        var active = await ActiveAsync(ct);
        if (Switched && await OtherAsync(ct) is { } old && await active.HeadAsync(path, ct) is null && await old.HeadAsync(path, ct) is not null)
            return await old.GenerateDownloadUrlAsync(path, headers, ct);
        return await active.GenerateDownloadUrlAsync(path, headers, ct);
    }

    public async Task<ArchiveObjectInfo?> HeadAsync(string path, CancellationToken ct = default)
    {
        var info = await (await ActiveAsync(ct)).HeadAsync(path, ct);
        if (info is null && Switched && await OtherAsync(ct) is { } old) info = await old.HeadAsync(path, ct);   // uploaded through a URL from before the switch
        return info;
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await (await ActiveAsync(ct)).DeleteAsync(path, ct);
        if (await OtherAsync(ct) is { } other) await other.DeleteAsync(path, ct);
    }

    public async Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = paths.ToList();
        await (await ActiveAsync(ct)).DeleteObjectsAsync(list, ct);
        if (await OtherAsync(ct) is { } other) await other.DeleteObjectsAsync(list, ct);
    }

    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var o in (await ActiveAsync(ct)).ListObjectsAsync(prefix, ct)) yield return o;
    }

    public async Task<string> CreateMultipartUploadAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default)
    {
        var active = await ActiveAsync(ct);
        var id = await active.CreateMultipartUploadAsync(path, mimeType, sizeBytes, ct);
        if (_uploads.Count > 10_000) _uploads.Clear();   // abandoned uploads aren't removed otherwise; losing these only costs a lookup
        _uploads[id] = active;
        return id;
    }

    /// <summary>Where a multipart upload lives: remembered on this server, else found by asking (after a switch).</summary>
    private async Task<IArchive> UploadAsync(string path, string uploadId, CancellationToken ct)
    {
        if (_uploads.TryGetValue(uploadId, out var known)) return known;
        var active = await ActiveAsync(ct);
        if (!Switched || await OtherAsync(ct) is not { } old) return active;
        try { await active.ListPartsAsync(path, uploadId, ct); return _uploads[uploadId] = active; }
        catch (Exception) when (!ct.IsCancellationRequested) { return _uploads[uploadId] = old; }
    }

    public async Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default) =>
        await (await UploadAsync(path, uploadId, ct)).SignPartAsync(path, uploadId, partNumber, ct);

    public async Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        var archive = await UploadAsync(path, uploadId, ct);
        _uploads.TryRemove(uploadId, out _);
        return await archive.CompleteMultipartUploadAsync(path, uploadId, ct);
    }

    public async Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default) =>
        await (await UploadAsync(path, uploadId, ct)).ListPartsAsync(path, uploadId, ct);

    public async Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        var archive = await UploadAsync(path, uploadId, ct);
        _uploads.TryRemove(uploadId, out _);
        await archive.AbortMultipartUploadAsync(path, uploadId, ct);
    }

    public async Task<int> DropAbandonedUploadsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        var dropped = (await ActiveAsync(ct)) is IAbandonedUploads active ? await active.DropAbandonedUploadsAsync(olderThanUtc, ct) : 0;
        if (await OtherAsync(ct) is IAbandonedUploads other) dropped += await other.DropAbandonedUploadsAsync(olderThanUtc, ct);
        return dropped;
    }

    public async Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var stream = await ((await ActiveAsync(ct)) as IArchiveContent ?? throw NoContent()).OpenReadAsync(path, ct);
        if (stream is null && Switched && await OtherAsync(ct) is IArchiveContent old) stream = await old.OpenReadAsync(path, ct);
        return stream;
    }

    public async Task WriteAsync(string path, Stream content, string? contentType, CancellationToken ct = default) =>
        await ((await ActiveAsync(ct)) as IArchiveContent ?? throw NoContent()).WriteAsync(path, content, contentType, ct);

    private static NotSupportedException NoContent() => new("This storage location can't read or write bytes from the server.");
}
