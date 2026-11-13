using Npgsql;
using NpgsqlTypes;
using System.Text.Json.Nodes;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class UpdateMetadataOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    /// <summary>
    /// The metadata after a patch: each key set to its new value, a null value removing the key (as Firebase's
    /// custom metadata).
    /// </summary>
    internal static JsonObject Merge(JsonObject current, JsonObject patch)
    {
        var merged = (JsonObject)current.DeepClone();
        foreach (var (key, value) in patch)
        {
            if (value is null) merged.Remove(key);
            else merged[key] = value.DeepClone();
        }
        return merged;
    }

    /// <summary>Stores <paramref name="metadata"/> (the merged result) on the file at <paramref name="version"/>; null when it changed.</summary>
    internal async Task<FileRecord?> ExecuteAsync(string path, long version, JsonObject metadata, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE {WincheStorageTables.Files}
            SET metadata = @metadata::jsonb, updated_at = NOW(), version = version + 1
            WHERE path = @path AND version = @version
            RETURNING *
            """;
        cmd.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = metadata.ToJsonString() });
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("version", version);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadSingleAsync(reader, ct);
    }
}
