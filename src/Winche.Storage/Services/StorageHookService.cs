using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Winche.Core.Feeds;
using Winche.Rules.Matching;
using Winche.Storage.Abstraction;
using Winche.Storage.Constants;
using Winche.Storage.Models;

namespace Winche.Storage.Services;

/// <summary>
/// Delivers file changes to hooks: each hook (type and path pattern) is a durable consumer of storage's feed, run by
/// Winche.Core's <see cref="WincheFeedRunner{TRecord}"/> on one node at a time, in the order changes were committed,
/// at least once. A failing change is retried, holding that hook back, until it has failed for
/// <see cref="HookDeliveryOptions.MaxRetryAge"/>; then it's a dead letter (<see cref="StorageHooks"/>). A hook added
/// later starts from then. Also prunes the feed.
/// </summary>
internal sealed class StorageHookService(
    [FromKeyedServices(WincheStorageKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source,
    HookInvocationDispatcher dispatcher,
    HookDeliveryOptions? options = null,
    ILogger<StorageHookService>? logger = null) : BackgroundService
{
    private readonly HookDeliveryOptions _options = options ?? new HookDeliveryOptions();
    private readonly ILogger _logger = logger ?? NullLogger<StorageHookService>.Instance;
    private readonly WincheFeed<FeedEvent> _feed = StorageFeed.Open(source);

    /// <summary>One runner per hook consumer (registrations of the same hook type and pattern share one).</summary>
    internal IReadOnlyDictionary<string, WincheFeedRunner<FeedEvent>> Runners { get; } = dispatcher.Registrations
        .GroupBy(StorageFeed.ConsumerOf)
        .ToDictionary(g => g.Key, g => Runner(source, g.Key, g.First(), options ?? new HookDeliveryOptions(), logger ?? (ILogger)NullLogger.Instance));

    internal static WincheFeedRunner<FeedEvent> Runner(NpgsqlDataSource source, string consumer, HookRegistration registration,
        HookDeliveryOptions options, ILogger logger) =>
        new(StorageFeed.Open(source), consumer, (events, ct) => DeliverAsync(registration, events, ct), new WincheFeedOptions
        {
            BatchSize = options.BatchSize,
            DeadLetterAfter = options.MaxRetryAge,
        }, logger);

    /// <summary>Hands <paramref name="events"/> on the hook's paths to it, in order; says which one failed, if one does.</summary>
    internal static async Task DeliverAsync(HookRegistration registration, IReadOnlyList<FeedEvent> events, CancellationToken ct)
    {
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (!PathMatcher.IsMatch(registration.Path, e.Path)) continue;
            try
            {
                var record = e.Data is null ? null : e.Data.Deserialize<FileRecord>();
                var hook = registration.Hook;
                await (e.Type switch
                {
                    HookEventKind.Registered => hook.OnFileRegisteredAsync(record!, ct),
                    HookEventKind.UploadConfirmed => hook.OnUploadConfirmedAsync(record!, ct),
                    HookEventKind.MetadataUpdated => hook.OnMetadataUpdatedAsync(record!, ct),
                    HookEventKind.Deleted => hook.OnFileDeletedAsync(e.Path, ct),
                    _ => Task.CompletedTask,   // a kind this version doesn't know: nothing to call
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new FeedDeliveryException(i, ex); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Runners.Count == 0) return;
        await using var wake = new WincheFeedWake(source, WincheEventFeed.Channel(WincheStorageTables.Feed), _options.PollInterval, _logger);
        dispatcher.Woken += wake.Wake;   // a change on this node: no need to wait for the notification
        try
        {
            await Task.WhenAll(Runners.Values.Select(r => r.RunAsync(wake.WaitAsync, stoppingToken)).Append(PruneAsync(stoppingToken)));
        }
        finally { dispatcher.Woken -= wake.Wake; }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                // Kept while a hook still needs them, up to four times the retention.
                await _feed.PruneAsync(now - _options.Retention, now - 4 * _options.Retention, [.. Runners.Keys], ct);
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pruning storage's feed failed; trying again later");
                try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
