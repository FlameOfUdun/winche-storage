using System.Runtime.CompilerServices;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.IntegrationTests;

/// <summary>
/// Controllable <see cref="IArchive"/> for delete/purge/upload tests: records the keys/ids handed to
/// each operation, can be told to throw to simulate an archive outage, returns configurable upload
/// metadata, and serves a fixed object listing for the orphan sweep.
/// </summary>
internal sealed class FakeArchive : IArchive
{
    // Delete / purge
    public bool ThrowOnDeleteObjects { get; set; }
    public List<string> DeletedKeys { get; } = [];
    public List<ArchivedObject> Objects { get; } = [];

    // Upload lifecycle knobs
    public string? SingleObjectETag { get; set; } = "etag-single";
    public string? MultipartETag { get; set; } = "etag-multipart";
    public string NextUploadId { get; set; } = "upload-1";
    public List<long> PartSizes { get; set; } = [];

    // Recorded calls
    public List<string> AbortedUploadIds { get; } = [];
    public List<string> CompletedUploadIds { get; } = [];
    public List<string> CreatedUploadPaths { get; } = [];

    public Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        if (ThrowOnDeleteObjects) throw new InvalidOperationException("archive down");
        DeletedKeys.AddRange(paths);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(
        string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var o in Objects)
            if (string.IsNullOrEmpty(prefix) || o.Key.StartsWith(prefix, StringComparison.Ordinal))
                yield return o;
        await Task.CompletedTask;
    }

    public Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        lock (AbortedUploadIds) AbortedUploadIds.Add(uploadId);
        return Task.CompletedTask;
    }

    /// <summary>The stored object's size; by default the size its upload link was made for (as an honest client sends).</summary>
    public long? SingleObjectSize { get; set; }
    private readonly Dictionary<string, long> _declared = [];

    /// <summary>Runs while the stored object is being looked at (to change the file meanwhile).</summary>
    public Func<Task>? OnHead { get; set; }

    public async Task<ArchiveObjectInfo?> HeadAsync(string path, CancellationToken ct = default)
    {
        if (OnHead is { } during) await during();
        return SingleObjectETag is null ? null
            : new ArchiveObjectInfo(SingleObjectSize ?? _declared.GetValueOrDefault(path), SingleObjectETag);
    }

    /// <summary>When set, each multipart upload gets its own id and waits until this many have started (a race).</summary>
    public int? StartTogether { get; set; }
    private int _started;
    private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> CreateMultipartUploadAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default)
    {
        lock (CreatedUploadPaths) CreatedUploadPaths.Add(path);
        if (StartTogether is not { } n) return NextUploadId;
        var id = $"upload-{Interlocked.Increment(ref _started)}";
        if (_started >= n) _allStarted.TrySetResult();
        await _allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        return id;
    }

    public Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        CompletedUploadIds.Add(uploadId);
        return Task.FromResult(MultipartETag);
    }

    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default)
    {
        _declared[path] = sizeBytes;
        return Task.FromResult(new UploadSession { Url = $"https://archive/put/{path}" });
    }

    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default)
        => Task.FromResult(new UploadSession { Url = $"https://archive/part/{path}/{partNumber}" });

    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default)
        => Task.FromResult(new DownloadSession { Url = $"https://archive/get/{path}", ExpiresAt = default });

    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default)
        => Task.FromResult<IEnumerable<FilePart>>(PartSizes.Select((size, i) => new FilePart { Number = i + 1, Size = size }).ToList());

    public Task DeleteAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
}
