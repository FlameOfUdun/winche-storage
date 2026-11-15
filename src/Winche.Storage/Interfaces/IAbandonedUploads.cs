namespace Winche.Storage.Interfaces;

/// <summary>
/// An archive that keeps unfinished multipart uploads itself (S3 does, with a bucket lifecycle rule; the folder archive
/// doesn't): the orphan sweep asks it to drop those untouched since <c>olderThanUtc</c>.
/// </summary>
public interface IAbandonedUploads
{
    /// <summary>Drops multipart uploads with no activity since <paramref name="olderThanUtc"/>; returns how many.</summary>
    Task<int> DropAbandonedUploadsAsync(DateTime olderThanUtc, CancellationToken ct = default);
}
