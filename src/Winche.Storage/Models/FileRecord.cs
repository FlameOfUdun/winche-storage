using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Winche.Storage.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UploadStatus : byte
{
    [JsonStringEnumMemberName("pending")]
    Pending = 0,

    [JsonStringEnumMemberName("complete")]
    Complete = 1,
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

    /// <summary>The archive's id for the multipart upload in progress, if any: server-side only.</summary>
    [JsonIgnore]
    internal string? UploadId { get; init; }

    /// <summary>How downloads are offered (<c>attachment; filename=...</c>, <c>inline</c>), if set.</summary>
    [JsonPropertyName("contentDisposition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentDisposition { get; init; }

    /// <summary>How long downloads may be cached (<c>public, max-age=3600</c>), if set.</summary>
    [JsonPropertyName("cacheControl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CacheControl { get; init; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    /// <summary>
    /// Where the file's confirmed bytes are in the archive: a key of their own, never reused, so nothing else ever
    /// writes there. Null until the first upload is confirmed.
    /// </summary>
    [JsonIgnore]
    internal string? ObjectKey { get; init; }

    /// <summary>Where the pending upload goes: a new key for each registration, so an old upload link can't reach it.</summary>
    [JsonIgnore]
    internal string? UploadKey { get; init; }
}
