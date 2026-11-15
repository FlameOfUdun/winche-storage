using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class ConfirmUploadOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    /// <summary>
    /// Marks the file complete (its upload key becomes its object key), but only the one that was checked: still pending, at <paramref name="version"/>. Null
    /// when it changed meanwhile (overwritten, deleted, or confirmed by another request).
    /// </summary>
    internal async Task<FileRecord?> ExecuteAsync(string path, long version, string? contentHash, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE {WincheStorageTables.Files}
            SET upload_status = @complete, upload_id = NULL, content_hash = @hash, object_key = upload_key, upload_key = NULL, updated_at = NOW()
            WHERE path = @path AND version = @version AND upload_status = @pending
            RETURNING *
            """;
        cmd.Parameters.AddWithValue("complete", (short)UploadStatus.Complete);
        cmd.Parameters.AddWithValue("pending", (short)UploadStatus.Pending);
        cmd.Parameters.AddWithValue("hash", (object?)contentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("version", version);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadSingleAsync(reader, ct);
    }
}
