using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class ConfirmUploadOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    internal async Task<FileRecord?> ExecuteAsync(string path, string? contentHash, CancellationToken ct)
    {
        var info = FilePathParser.Parse(path);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE {WincheTables.Files}
            SET upload_status = @status, updated_at = NOW(), content_hash = @hash
            WHERE path = @path
            RETURNING *
            """;

        cmd.Parameters.AddWithValue("status", (short)UploadStatus.Complete);
        cmd.Parameters.AddWithValue("hash", (object?)contentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("path", path);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadSingleAsync(reader, ct);
    }
}
