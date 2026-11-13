using System.Text.Json.Serialization;

namespace Winche.Storage.Models;

/// <summary>
/// How downloads of a file are served, as Firebase's settable metadata: <see cref="ContentDisposition"/> (e.g.
/// <c>attachment; filename="report.pdf"</c>) and <see cref="CacheControl"/> (e.g. <c>public, max-age=3600</c>). In an
/// update, null leaves a header as it is and <c>""</c> clears it.
/// </summary>
public sealed record FileHeaders(
    [property: JsonPropertyName("contentDisposition")] string? ContentDisposition = null,
    [property: JsonPropertyName("cacheControl")] string? CacheControl = null)
{
    internal static string? Apply(string? current, string? change) => change switch { null => current, "" => null, _ => change };
}

/// <summary>What an archive puts on a download: the file's type and its <see cref="FileHeaders"/>.</summary>
public sealed record DownloadHeaders(string ContentType, string? ContentDisposition = null, string? CacheControl = null);
