using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class ListFilesOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    /// <summary>The files directly in <paramref name="directory"/> named after <paramref name="after"/>, in name order.</summary>
    public async Task<List<FileRecord>> ExecuteAsync(string directory, string? mimeType, string? after, int limit, CancellationToken ct)
    {
        var sql = $"""
            SELECT *
            FROM {WincheStorageTables.Files}
            WHERE directory = @directory
              AND (@mimeType::text IS NULL OR mime_type = @mimeType::text)
              AND (@after::text IS NULL OR id > @after::text COLLATE "C")
            ORDER BY id COLLATE "C"
            LIMIT @limit
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("directory", directory);
        cmd.Parameters.Add(new NpgsqlParameter("mimeType", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)mimeType ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)after ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadAllAsync(reader, ct);
    }
}
