using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class GetFileOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    internal async Task<FileRecord?> ExecuteAsync(string path, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT *
            FROM {WincheTables.Files}
            WHERE path = @path
            """;
        cmd.Parameters.AddWithValue("path", path);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadSingleAsync(reader, ct);
    }
}
