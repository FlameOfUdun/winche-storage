using Npgsql;
using Winche.Storage.Constants;

namespace Winche.Storage.Operations;

internal sealed class SetUploadIdOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    /// <summary>Forgets the file's multipart upload, if it's still <paramref name="uploadId"/>.</summary>
    internal async Task ClearAsync(string path, string uploadId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {WincheStorageTables.Files} SET upload_id = NULL WHERE path = @path AND upload_id = @uploadId";
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("uploadId", uploadId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Records <paramref name="uploadId"/> as the file's multipart upload, only if it still has none and is the pending
    /// file at <paramref name="version"/>: of two requests starting an upload at once, one wins.
    /// </summary>
    internal async Task<bool> StartAsync(string path, long version, string uploadId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE {WincheStorageTables.Files} SET upload_id = @uploadId
            WHERE path = @path AND version = @version AND upload_id IS NULL AND upload_status = 0
            """;
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("uploadId", uploadId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
