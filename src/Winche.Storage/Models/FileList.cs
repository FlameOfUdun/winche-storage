using System.Text.Json.Serialization;

namespace Winche.Storage.Models;

/// <summary>What to list: at most <see cref="MaxResults"/> entries (default and cap 1,000), from <see cref="PageToken"/>.</summary>
public sealed record FileListOptions(int? MaxResults = null, string? PageToken = null, string? MimeType = null);

/// <summary>
/// One page of a folder, as Firebase's <c>list()</c>: the files directly in it and its sub-folders (as full paths), in
/// name order; <see cref="NextPageToken"/> fetches the rest, null when there's no more.
/// </summary>
public sealed record FileListResult(
    [property: JsonPropertyName("items")] IReadOnlyList<FileRecord> Items,
    [property: JsonPropertyName("prefixes")] IReadOnlyList<string> Prefixes,
    [property: JsonPropertyName("nextPageToken")] string? NextPageToken);
