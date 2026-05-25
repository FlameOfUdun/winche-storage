using Npgsql;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class ConfirmUploadOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
{
    internal async Task<FileRecord?> ExecuteAsync(string path, CancellationToken ct)
    {
        var info = FilePathParser.Parse(path);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE {table}
            SET upload_status = @status, updated_at = NOW()
            WHERE path = @path
            RETURNING *
            """;

        cmd.Parameters.AddWithValue("status", (short)UploadStatus.Complete);
        cmd.Parameters.AddWithValue("path", path);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadSingleAsync(reader, ct);
    }
}
 