using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Winche.Storage.Abstraction;
using Winche.Storage.Constants;
using Winche.Storage.Models;

namespace Winche.Storage.Services;

/// <summary>
/// Runs the hook deliveries <see cref="HookOutbox"/> wrote. Every node runs one; <c>FOR UPDATE SKIP LOCKED</c> plus a
/// lease give each delivery to exactly one node at a time. Deliveries for the same hook and file run in the order the
/// changes were made. A failure is retried with backoff until <see cref="HookDeliveryOptions.MaxRetryAge"/>, then
/// kept as a dead letter. At least once: a hook may see an event again (a node died mid-call, or the lease ran out),
/// so hooks with side effects should be idempotent.
/// </summary>
public sealed class HookDeliveryService(
    [FromKeyedServices(WincheStorageKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source,
    HookInvocationDispatcher dispatcher,
    HookDeliveryOptions? options = null,
    ILogger<HookDeliveryService>? logger = null) : BackgroundService
{
    private readonly HookDeliveryOptions _options = options ?? new HookDeliveryOptions();
    private readonly ILogger _logger = logger ?? NullLogger<HookDeliveryService>.Instance;
    private readonly Dictionary<string, FileStoreHook> _hooks = dispatcher.Registrations
        .GroupBy(HookOutbox.KeyOf).ToDictionary(g => g.Key, g => g.First().Hook, StringComparer.Ordinal);
    private DateTimeOffset _nextHousekeeping = DateTimeOffset.MinValue;

    private const string T = WincheStorageTables.HookDeliveries;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_hooks.Count == 0) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            int ran;
            try
            {
                ran = await RunOnceAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= _nextHousekeeping)
                {
                    await HousekeepAsync(stoppingToken);
                    _nextHousekeeping = DateTimeOffset.UtcNow.AddMinutes(10);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hook delivery round failed; retrying");
                ran = 0;
            }

            if (ran < _options.BatchSize)
            {
                try { await dispatcher.WaitForWakeAsync(_options.PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Claims and runs one batch of due deliveries; returns how many it claimed.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(ct);
        await Parallel.ForEachAsync(claimed, new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallel, CancellationToken = CancellationToken.None },
            async (d, _) => await DeliverAsync(d, ct));
        return claimed.Count;
    }

    private sealed record Delivery(long Id, string Hook, string Kind, string Path, string? Record, int Attempts);

    private async Task<List<Delivery>> ClaimAsync(CancellationToken ct)
    {
        // A delivery waits while an earlier one for the same hook and file is still pending (claimed or retrying),
        // which keeps each file's events in order per hook. Dead letters don't hold later ones back.
        await using var cmd = source.CreateCommand($"""
            WITH due AS (
                SELECT d.id FROM {T} d
                WHERE d.hook = ANY($1) AND d.dead_at IS NULL AND d.next_attempt_at <= now()
                  AND NOT EXISTS (SELECT 1 FROM {T} e
                                  WHERE e.hook = d.hook AND e.path = d.path AND e.id < d.id AND e.dead_at IS NULL)
                ORDER BY d.id
                LIMIT $2
                FOR UPDATE OF d SKIP LOCKED)
            UPDATE {T} t SET next_attempt_at = now() + $3, attempts = t.attempts + 1
            FROM due WHERE t.id = due.id
            RETURNING t.id, t.hook, t.kind, t.path, t.record::text, t.attempts
            """);
        cmd.Parameters.AddWithValue(_hooks.Keys.ToArray());
        cmd.Parameters.AddWithValue(_options.BatchSize);
        cmd.Parameters.AddWithValue(_options.Lease);
        var list = new List<Delivery>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Delivery(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5)));
        return list;
    }

    private async Task DeliverAsync(Delivery d, CancellationToken ct)
    {
        try
        {
            var hook = _hooks[d.Hook];
            var record = d.Record is null ? null : JsonSerializer.Deserialize<FileRecord>(d.Record);
            await (d.Kind switch
            {
                HookEventKind.Registered => hook.OnFileRegisteredAsync(record!, ct),
                HookEventKind.UploadConfirmed => hook.OnUploadConfirmedAsync(record!, ct),
                HookEventKind.MetadataUpdated => hook.OnMetadataUpdatedAsync(record!, ct),
                HookEventKind.Deleted => hook.OnFileDeletedAsync(d.Path, ct),
                _ => throw new InvalidOperationException($"Unknown hook event '{d.Kind}'."),
            });
            await ExecAsync($"DELETE FROM {T} WHERE id = $1", [d.Id]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: hand it back untouched, for this or another node.
            await TryExecAsync($"UPDATE {T} SET next_attempt_at = now(), attempts = attempts - 1 WHERE id = $1", [d.Id]);
        }
        catch (Exception ex)
        {
            var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, d.Attempts - 1), _options.MaxBackoff.TotalSeconds));
            _logger.LogWarning(ex, "Hook {Hook} failed for {Kind} {Path} (attempt {Attempt})", d.Hook, d.Kind, d.Path, d.Attempts);
            await TryExecAsync($"""
                UPDATE {T} SET last_error = $2,
                    dead_at = CASE WHEN now() - created_at >= $3 THEN now() END,
                    next_attempt_at = now() + $4
                WHERE id = $1
                """, [d.Id, ex.ToString(), _options.MaxRetryAge, backoff]);
        }
    }

    private async Task HousekeepAsync(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($"""
            DELETE FROM {T} WHERE (dead_at IS NOT NULL OR hook <> ALL($1)) AND created_at < now() - $2
            """);
        cmd.Parameters.AddWithValue(_hooks.Keys.ToArray());
        cmd.Parameters.AddWithValue(_options.Retention);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecAsync(string sql, object[] args)
    {
        await using var cmd = source.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task TryExecAsync(string sql, object[] args)
    {
        try { await ExecAsync(sql, args); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not record a hook delivery's outcome; it will be retried after its lease"); }
    }
}
