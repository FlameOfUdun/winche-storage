using Npgsql;
using NpgsqlTypes;
using System.Text.Json.Nodes;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class InsertFileOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    /// <summary>Registers a new file, pending its upload; null when one appeared at the path meanwhile.</summary>
    internal async Task<FileRecord?> InsertAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata, CancellationToken ct)
    {
        var info = FilePathParser.Parse(path);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {WincheStorageTables.Files} (id, directory, path, metadata, mime_type, size_bytes, upload_status, created_at, updated_at, version, upload_key)
            VALUES (@id, @directory, @path, @metadata::jsonb, @mimeType, @sizeBytes, @pending, NOW(), NOW(), 1, @uploadKey)
            ON CONFLICT (path) DO NOTHING
            RETURNING *
            """;
        cmd.Parameters.AddWithValue("id", info.Id!);
        cmd.Parameters.AddWithValue("directory", info.Directory);
        cmd.Parameters.AddWithValue("path", path);
        Fill(cmd, path, mimeType, sizeBytes, metadata);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadSingleAsync(reader, ct);
    }

    /// <summary>
    /// Replaces the file at <paramref name="path"/> (at <paramref name="version"/>) with a new one pending its upload:
    /// the old upload, its multipart upload id and its content hash are forgotten, so only new bytes can complete it.
    /// Null when the file changed meanwhile.
    /// </summary>
    internal async Task<FileRecord?> ReplaceAsync(string path, long version, string mimeType, long sizeBytes, JsonObject? metadata, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE {WincheStorageTables.Files}
            SET metadata = @metadata::jsonb, mime_type = @mimeType, size_bytes = @sizeBytes, upload_status = @pending,
                upload_id = NULL, content_hash = NULL, upload_key = @uploadKey, updated_at = NOW(), version = version + 1
            WHERE path = @path AND version = @version
            RETURNING *
            """;
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("version", version);
        Fill(cmd, path, mimeType, sizeBytes, metadata);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadSingleAsync(reader, ct);
    }

    private static void Fill(NpgsqlCommand cmd, string path, string mimeType, long sizeBytes, JsonObject? metadata)
    {
        cmd.Parameters.AddWithValue("uploadKey", ObjectKeys.New(path));
        cmd.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = (metadata ?? []).ToJsonString() });
        cmd.Parameters.AddWithValue("mimeType", mimeType);
        cmd.Parameters.AddWithValue("sizeBytes", sizeBytes);
        cmd.Parameters.AddWithValue("pending", (short)UploadStatus.Pending);
    }
}
