using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Storage.Constants;

namespace Winche.Storage.Services;

/// <summary>A hook delivery that kept failing for <c>HookDeliveryOptions.MaxRetryAge</c>.</summary>
public sealed record StorageHookDeadLetter(
    long Id, string Hook, string Kind, string Path, int Attempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset DeadAt);

/// <summary>Look at, retry or discard hook deliveries that gave up. Kept for <c>HookDeliveryOptions.Retention</c>.</summary>
public sealed class StorageHookDeadLetters([FromKeyedServices(WincheStorageKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source)
{
    private const string T = WincheStorageTables.HookDeliveries;

    public async Task<IReadOnlyList<StorageHookDeadLetter>> ListAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand(
            $"SELECT id, hook, kind, path, attempts, last_error, created_at, dead_at FROM {T} WHERE dead_at IS NOT NULL ORDER BY id LIMIT $1");
        cmd.Parameters.AddWithValue(limit);
        var list = new List<StorageHookDeadLetter>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new StorageHookDeadLetter(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7)));
        return list;
    }

    /// <summary>Queues dead letters again, with a fresh retry window. Null <paramref name="id"/>: all of them.</summary>
    public async Task<int> RetryAsync(long? id = null, CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand($"""
            UPDATE {T} SET dead_at = NULL, attempts = 0, last_error = NULL, created_at = now(), next_attempt_at = now()
            WHERE dead_at IS NOT NULL AND ($1::bigint IS NULL OR id = $1)
            """);
        cmd.Parameters.AddWithValue((object?)id ?? DBNull.Value);
        cmd.Parameters[0].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Discards dead letters. Null <paramref name="id"/>: all of them.</summary>
    public async Task<int> DeleteAsync(long? id = null, CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand($"DELETE FROM {T} WHERE dead_at IS NOT NULL AND ($1::bigint IS NULL OR id = $1)");
        cmd.Parameters.AddWithValue((object?)id ?? DBNull.Value);
        cmd.Parameters[0].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// How hook delivery is doing: deliveries waiting (and the oldest one's age), how many have failed at least once,
    /// and the dead letters. For health checks and the console.
    /// </summary>
    public async Task<StorageHookStatus> StatusAsync(CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand($"""
            SELECT count(*) FILTER (WHERE dead_at IS NULL),
                   min(created_at) FILTER (WHERE dead_at IS NULL),
                   count(*) FILTER (WHERE dead_at IS NULL AND attempts > 0 AND last_error IS NOT NULL),
                   count(*) FILTER (WHERE dead_at IS NOT NULL)
            FROM {WincheStorageTables.HookDeliveries}
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new StorageHookStatus(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1), r.GetInt64(2), r.GetInt64(3));
    }
}

/// <summary>Hook delivery at a glance: waiting deliveries, the oldest one's time, those failing, and dead letters.</summary>
public sealed record StorageHookStatus(long Pending, DateTimeOffset? OldestPending, long Failing, long DeadLetters);
