using Npgsql;
using NpgsqlTypes;
using System.Text.Json.Nodes;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class InsertFileOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
{
    internal async Task<FileRecord> ExecuteAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata, CancellationToken ct)
    {
        var info = FilePathParser.Parse(path);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {table} (id, directory, path, metadata, mime_type, size_bytes, upload_status, created_at, updated_at, version)
            VALUES (@id, @directory, @path, @metadata::jsonb, @mimeType, @sizeBytes, @uploadStatus, NOW(), NOW(), 1)
            ON CONFLICT (path) DO UPDATE SET
                id         = EXCLUDED.id,
                directory  = EXCLUDED.directory,
                path       = EXCLUDED.path,
                metadata   = EXCLUDED.metadata,
                mime_type  = EXCLUDED.mime_type,
                size_bytes = EXCLUDED.size_bytes,
                upload_status = EXCLUDED.upload_status,
                updated_at = NOW(),
                version    = {table}.version + 1
            RETURNING *
            """;

        cmd.Parameters.AddWithValue("id", info.Id!);
        cmd.Parameters.AddWithValue("directory", info.Directory);
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = (metadata ?? []).ToJsonString() });
        cmd.Parameters.AddWithValue("mimeType", mimeType);
        cmd.Parameters.AddWithValue("sizeBytes", sizeBytes);
        cmd.Parameters.AddWithValue("uploadStatus", (short)UploadStatus.Pending);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadSingleAsync(reader, ct)
            ?? throw new InvalidOperationException("Insert returned no rows.");
    }
}
