namespace Winche.Storage.Models;

/// <summary>
/// Result of <c>FileStorage.ListDirectoryIdsAsync</c>:
/// the distinct, lexicographically-ordered immediate sub-directory ids for one page,
/// plus an opaque <see cref="NextPageToken"/> (null when there are no more pages).
/// </summary>
public sealed record ListDirectoryIdsResult(
    IReadOnlyList<string> DirectoryIds,
    string? NextPageToken);
