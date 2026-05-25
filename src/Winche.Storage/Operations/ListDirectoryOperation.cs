using Npgsql;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class ListDirectoryOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
{
    internal async Task<List<FileRecord>?> ExecuteAsync(string directory, string? mimeType = null, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT *
            FROM {table}
            WHERE directory = @directory AND ({(mimeType == null ? "TRUE" : "mime_type = @mimeType")})
            """;
        cmd.Parameters.AddWithValue("directory", directory);
        if (mimeType != null)
            cmd.Parameters.AddWithValue("mimeType", mimeType);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadAllAsync(reader, ct);
    }
}