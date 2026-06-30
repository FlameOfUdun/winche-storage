using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Winche.Storage.DependencyInjection;

namespace Winche.Storage.Services;

/// <summary>
/// Background sweep that periodically calls <see cref="FileStorage.PurgeOrphansAsync"/> to reclaim
/// archive objects with no matching database row. Registered via
/// <see cref="WincheStorageOptions.UseOrphanSweep"/>. Best-effort: a failed cycle is swallowed and
/// the loop waits for the next tick, so a transient DB or archive outage never tears down the host.
/// </summary>
internal sealed class OrphanSweepService(
    FileStorage storage,
    IOptions<OrphanSweepOptions> options
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
                await storage.PurgeOrphansAsync(opts.Prefix, opts.GraceWindow, stoppingToken);
            }
            catch (OperationCanceledException)
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
