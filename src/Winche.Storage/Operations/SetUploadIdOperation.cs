using Npgsql;

namespace Winche.Storage.Operations;

internal sealed class SetUploadIdOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
{
    internal async Task ExecuteAsync(string path, string? uploadId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE {table}
            SET upload_id = @uploadId
            WHERE path = @path
            """;
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.Add(new NpgsqlParameter("uploadId", uploadId ?? (object)DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
