using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class GetFileOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    /// <summary>The file at <paramref name="path"/>; with <paramref name="forUpdate"/>, locked until the transaction ends.</summary>
    internal async Task<FileRecord?> ExecuteAsync(string path, CancellationToken ct, bool forUpdate = false)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT * FROM {WincheStorageTables.Files} WHERE path = @path{(forUpdate ? " FOR UPDATE" : "")}";
        cmd.Parameters.AddWithValue("path", path);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadSingleAsync(reader, ct);
    }
}
