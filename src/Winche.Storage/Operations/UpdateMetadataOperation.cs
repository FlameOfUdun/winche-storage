using Npgsql;
using NpgsqlTypes;
using System.Text.Json.Nodes;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class UpdateMetadataOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    internal async Task<FileRecord?> ExecuteAsync(string path, JsonObject metadata, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE {WincheTables.Files}
            SET metadata   = @metadata::jsonb,
                updated_at = NOW(),
                version    = {WincheTables.Files}.version + 1
            WHERE path = @path
            RETURNING *
            """;
        cmd.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = metadata.ToJsonString() });
        cmd.Parameters.AddWithValue("path", path);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadSingleAsync(reader, ct);
    }
}
