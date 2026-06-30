using Npgsql;
using NpgsqlTypes;
using Winche.Storage.Constants;

namespace Winche.Storage.Operations;

/// <summary>
/// Returns the subset of the supplied paths that currently have a row in <c>winche_files</c>.
/// Used by the orphan sweep to decide which archive keys are unreferenced.
/// </summary>
internal sealed class SelectExistingPathsOperation(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    internal async Task<HashSet<string>> ExecuteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var existing = new HashSet<string>();
        if (paths.Count == 0) return existing;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT path
            FROM {WincheTables.Files}
            WHERE path = ANY(@paths)
            """;
        cmd.Parameters.Add(new NpgsqlParameter("paths", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = paths as string[] ?? [.. paths],
        });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            existing.Add(reader.GetString(0));
        return existing;
    }
}
