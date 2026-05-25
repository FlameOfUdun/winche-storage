using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Winche.Storage.Models;

[JsonConverter(typeof(JsonStringEnumConverter<UploadStatus>))]
public enum UploadStatus : byte
{
    Pending = 0,
    Complete = 1,
    Failed = 2
}

public sealed record FileRecord
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("directory")]
    public required string Directory { get; init; }

    [JsonPropertyName("path")]  
    public required string Path { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTime UpdatedAt { get; init; }

    [JsonPropertyName("metadata")]
    public required JsonObject MetaData { get; init; } = [];

    [JsonPropertyName("version")]
    public required long Version { get; init; }

    [JsonPropertyName("mimeType")]
    public required string MimeType { get; init; }

    [JsonPropertyName("sizeBytes")]
    public required long SizeBytes { get; init; }

    [JsonPropertyName("uploadStatus")]
    public required UploadStatus UploadStatus { get; init; }

    [JsonPropertyName("uploadId")]
    public string? UploadId { get; init; }
}
