using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.Coordination;
using Winche.Storage.DependencyInjection;

namespace Winche.Storage.Services;

/// <summary>
/// Background sweep that periodically calls <see cref="FileStorage.PurgeOrphansAsync"/> to reclaim
/// archive objects with no matching database row. Registered via
/// <see cref="WincheStorageOptions.UseOrphanSweep"/>. Best-effort: a failed cycle is swallowed and
/// the loop waits for the next tick, so a transient DB or archive outage never tears down the host.
/// With several nodes, only the one that gets the sweep's advisory lock sweeps on a given tick.
/// </summary>
internal sealed class OrphanSweepService(
    FileStorage storage,
    IOptions<OrphanSweepOptions> options,
    [FromKeyedServices(WincheStorageKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        using var timer = new PeriodicTimer(opts.Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var lease = await AdvisoryLease.TryAcquireAsync(source, "storage-orphan-sweep", TimeSpan.FromSeconds(30), stoppingToken);
                if (lease is null) continue; // another node is sweeping
                using var active = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Lost);
                await storage.PurgeOrphansAsync(opts.Prefix, opts.GraceWindow, active.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Best-effort: swallow and retry on the next tick.
            }
        }
    }
}
