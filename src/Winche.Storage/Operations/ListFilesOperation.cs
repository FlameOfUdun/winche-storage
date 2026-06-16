using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class ListFilesOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    public async Task<IEnumerable<FileRecord>> ExecuteAsync(string directory, string? mimeType, CancellationToken ct)
    {
        var condition = mimeType is null ? "TRUE" : "mime_type = @mimeType";

        var sql = $"""
            SELECT *
            FROM {WincheTables.Files}
            WHERE directory = @directory AND {condition}
            ORDER BY path
            """;

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("directory", directory);
        cmd.Parameters.Add(new NpgsqlParameter("mimeType", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)mimeType ?? DBNull.Value
        });

        var records = new List<FileRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadAllAsync(reader, ct);
    }
}
