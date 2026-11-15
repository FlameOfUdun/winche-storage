using Npgsql;
using Winche.Core;
using Winche.Storage.Constants;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed class DeleteFileOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    /// <summary>The file at <paramref name="path"/> and every file under <c>path/</c>, locked until the transaction ends.</summary>
    internal async Task<IReadOnlyList<FileRecord>> SelectForUpdateAsync(string path, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT * FROM {WincheStorageTables.Files}
            WHERE path = @path OR path LIKE @prefix ESCAPE '\'
            ORDER BY path
            FOR UPDATE
            """;
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("prefix", WincheSql.EscapeLike(path) + "/%");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlFileReader.ReadAllAsync(reader, ct);
    }

    /// <summary>
    /// Deletes exactly <paramref name="paths"/> (the files selected and checked), never a file that appeared under the
    /// folder meanwhile.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ExecuteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {WincheStorageTables.Files} WHERE path = ANY(@paths) RETURNING path";
        cmd.Parameters.AddWithValue("paths", paths.ToArray());
        var deleted = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) deleted.Add(reader.GetString(0));
        return deleted;
    }
}
