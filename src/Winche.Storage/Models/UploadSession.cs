using System.Text.Json.Serialization;

namespace Winche.Storage.Models;

public sealed record UploadSession
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; init; }
}
