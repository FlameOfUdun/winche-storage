using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Winche.Rules.Matching;
using Winche.Storage.Abstraction;
using Winche.Storage.Constants;
using Winche.Storage.Models;

namespace Winche.Storage.Services;

/// <summary>The file events a hook is delivered durably (through <see cref="WincheStorageTables.HookDeliveries"/>).</summary>
internal static class HookEventKind
{
    public const string Registered = "registered";
    public const string UploadConfirmed = "upload_confirmed";
    public const string MetadataUpdated = "metadata_updated";
    public const string Deleted = "deleted";
}

/// <summary>Writes hook deliveries in the caller's transaction: one row per registration whose path matches.</summary>
internal static class HookOutbox
{
    /// <summary>A registration's stable name across restarts and nodes: hook type and path pattern.</summary>
    public static string KeyOf(HookRegistration r) => $"{r.Hook.GetType().FullName}|{r.Path}";

    public static async Task AddAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyCollection<HookRegistration> hooks,
        string kind, string path, FileRecord? record, CancellationToken ct)
    {
        var keys = hooks.Where(h => PathMatcher.IsMatch(h.Path, path)).Select(KeyOf).Distinct().ToArray();
        if (keys.Length == 0) return;

        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO {WincheStorageTables.HookDeliveries} (hook, kind, path, record)
            SELECT unnest(@hooks), @kind, @path, @record
            """, conn, tx);
        cmd.Parameters.AddWithValue("hooks", keys);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Jsonb)
            { Value = record is null ? DBNull.Value : JsonSerializer.Serialize(record) });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
