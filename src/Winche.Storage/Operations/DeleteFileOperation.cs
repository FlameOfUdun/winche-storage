using Npgsql;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;

namespace Winche.Storage.Operations;

internal sealed record FileDeleteCandidate(string Path, string? UploadId, UploadStatus UploadStatus);

internal sealed class DeleteFileOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
{
    internal async Task<IReadOnlyList<FileDeleteCandidate>> SelectForUpdateAsync(string path, CancellationToken ct)
    {
        if (!FilePathParser.IsValidPath(path, out var error))
            throw new ArgumentException(error);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT path, upload_id, upload_status
            FROM {table}
            WHERE path = @path OR path LIKE @prefix ESCAPE '\'
            FOR UPDATE
            """;
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("prefix", LikePatternEscaper.Escape(path) + "/%");

        var results = new List<FileDeleteCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var p = reader.GetString(0);
            var uploadId = await reader.IsDBNullAsync(1, ct) ? null : reader.GetString(1);
            var status = (UploadStatus)reader.GetInt16(2);
            results.Add(new FileDeleteCandidate(p, uploadId, status));
        }
        return results;
    }

    internal async Task<IReadOnlyList<string>> ExecuteAsync(string path, CancellationToken ct)
    {
        if (!FilePathParser.IsValidPath(path, out var error))
            throw new ArgumentException(error);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            DELETE FROM {table}
            WHERE path = @path OR path LIKE @prefix ESCAPE '\'
            RETURNING path
            """;
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("prefix", LikePatternEscaper.Escape(path) + "/%");

        var deleted = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            deleted.Add(reader.GetString(0));
        return deleted;
    }
}
