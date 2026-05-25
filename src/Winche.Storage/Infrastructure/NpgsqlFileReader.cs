using Npgsql;
using System.Text.Json.Nodes;
using Winche.Storage.Models;

namespace Winche.Storage.Infrastructure;

internal static class NpgsqlFileReader
{
    private static FileRecord FromReader(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Directory = reader.GetString(reader.GetOrdinal("directory")),
        Path = reader.GetString(reader.GetOrdinal("path")),
        MetaData = JsonNode.Parse(reader.GetString(reader.GetOrdinal("metadata")))?.AsObject() ?? [],
        CreatedAt = reader.GetFieldValue<DateTime>(reader.GetOrdinal("created_at")).ToUniversalTime(),
        UpdatedAt = reader.GetFieldValue<DateTime>(reader.GetOrdinal("updated_at")).ToUniversalTime(),
        Version = reader.GetInt64(reader.GetOrdinal("version")),
        MimeType = reader.GetString(reader.GetOrdinal("mime_type")),
        SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
        UploadStatus = (UploadStatus)reader.GetInt16(reader.GetOrdinal("upload_status")),
        UploadId = reader.IsDBNull(reader.GetOrdinal("upload_id")) ? null : reader.GetString(reader.GetOrdinal("upload_id")),
    };

    internal static async Task<FileRecord?> ReadSingleAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct)) return null;
        return FromReader(reader);
    }

    internal static async Task<List<FileRecord>> ReadAllAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        var results = new List<FileRecord>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(FromReader(reader));
        }
        return results;
    }
}
