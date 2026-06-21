using Npgsql;
using NpgsqlTypes;
using Winche.Storage.Constants;

namespace Winche.Storage.Operations;

/// <summary>
/// Performs a descendant-scan of the <c>winche_files</c> table to return the distinct
/// immediate sub-directory names directly under a given parent directory.
///
/// A sub-directory X exists under D iff some file has <c>directory == "D/X"</c> or
/// <c>directory</c> starting with <c>"D/X/"</c>. Files sitting directly in D
/// (directory == D) are excluded because the range predicate begins at <c>"D/"</c>,
/// which is strictly greater than <c>"D"</c> under COLLATE "C".
///
/// COLLATE "C" is applied to all comparisons and ordering so results follow UTF-8
/// byte order and keyset pagination remains self-consistent across pages.
/// </summary>
internal sealed class ListDirectoryIdsOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    public async Task<IReadOnlyList<string>> ListAsync(
        string? parentDirectory, string? after, int limit, CancellationToken ct = default)
    {
        string cidExpr;
        string where;
        bool isRoot = string.IsNullOrEmpty(parentDirectory);

        if (isRoot)
        {
            cidExpr = "split_part(directory, '/', 1)";
            where = "directory <> ''";
        }
        else
        {
            cidExpr = "split_part(substr(directory, char_length(@lo) + 1), '/', 1)";
            where = "directory >= @lo COLLATE \"C\" AND directory < @hi COLLATE \"C\"";
        }

        var sql = $"""
            SELECT cid
            FROM (
                SELECT DISTINCT {cidExpr} AS cid
                FROM {WincheTables.Files}
                WHERE {where}
            ) sub
            WHERE (@after::text IS NULL OR cid > @after::text COLLATE "C")
            ORDER BY cid COLLATE "C"
            LIMIT @limit
            """;

        await using var cmd = new NpgsqlCommand(sql, conn, tx);

        if (!isRoot)
        {
            var lo = parentDirectory + "/";
            var hi = parentDirectory + (char)('/' + 1);
            cmd.Parameters.AddWithValue("lo", lo);
            cmd.Parameters.AddWithValue("hi", hi);
        }

        cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Text)
        {
            Value = (object?)after ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue("limit", limit);

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));

        return ids;
    }
}
