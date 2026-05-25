using System.Text.Json.Serialization;

namespace Winche.Storage.Models;

public sealed record DownloadSession
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("expiresAt")]
    public required DateTime ExpiresAt { get; init; }
}
