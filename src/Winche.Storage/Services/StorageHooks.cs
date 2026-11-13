using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Winche.Core.Feeds;
using Winche.Storage.Abstraction;
using Winche.Storage.Constants;

namespace Winche.Storage.Services;

/// <summary>Hook delivery at a glance: the oldest change a hook still has to take, how many hooks are failing, dead letters.</summary>
public sealed record StorageHookStatus(DateTimeOffset? OldestPending, int Failing, long DeadLetters);

/// <summary>
/// How file hooks are doing, and their dead letters: changes a hook gave up on after
/// <see cref="Models.HookDeliveryOptions.MaxRetryAge"/>. Kept in Winche.Core's shared feed tables (feed <c>storage</c>).
/// </summary>
public sealed class StorageHooks : IWincheFeedConsumers
{
    private readonly NpgsqlDataSource _source;
    private readonly WincheFeed<FeedEvent> _feed;
    private readonly IReadOnlyDictionary<string, HookRegistration> _consumers;

    internal StorageHooks([FromKeyedServices(WincheStorageKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source, HookInvocationDispatcher dispatcher)
    {
        _source = source;
        _feed = StorageFeed.Open(source);
        _consumers = dispatcher.Registrations.GroupBy(StorageFeed.ConsumerOf).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>Each hook's consumer: position, lag, failure, dead letters.</summary>
    public Task<IReadOnlyList<FeedConsumerStatus>> ConsumersAsync(CancellationToken ct = default) => _feed.GetStatusAsync([.. _consumers.Keys], ct);

    /// <summary>All hooks together, for health checks.</summary>
    public async Task<StorageHookStatus> StatusAsync(CancellationToken ct = default)
    {
        var all = await ConsumersAsync(ct);
        return new StorageHookStatus(all.Min(s => s.OldestPendingAt), all.Count(s => s.FailingSince is not null), all.Sum(s => s.DeadLetters));
    }

    /// <summary>The changes hooks gave up on, oldest first.</summary>
    public Task<IReadOnlyList<FeedDeadLetter>> DeadLettersAsync(int limit = 100, CancellationToken ct = default) =>
        _feed.GetDeadLettersAsync(limit: limit, ct: ct);

    /// <summary>Delivers a hook's dead letters again (every hook's, when <paramref name="consumer"/> is null).</summary>
    public async Task<FeedReplayResult> RetryAsync(string? consumer = null, CancellationToken ct = default)
    {
        int replayed = 0, failed = 0;
        foreach (var (name, registration) in _consumers.Where(c => consumer is null || c.Key == consumer))
        {
            var result = await _feed.ReplayDeadLettersAsync(name, (e, t) => StorageHookService.DeliverAsync(registration, [e], t), ct);
            replayed += result.Replayed;
            failed += result.Failed;
        }
        return new FeedReplayResult(replayed, failed);
    }

    /// <summary>
    /// Starts keeping changes for every registered hook that has no position yet: from now on, nothing is missed even
    /// before its delivery first runs (or while no node runs it). <c>InitializeWincheStorageAsync</c> calls it.
    /// </summary>
    public async Task TrackAsync(CancellationToken ct = default)
    {
        foreach (var name in _consumers.Keys)
            if (await _feed.GetPositionAsync(name, ct) is null)
                await _feed.SavePositionAsync(name, await _feed.GetHeadAsync(ct), ct);
    }

    /// <summary>Drops dead letters without delivering them: one, or all.</summary>
    public Task<int> DeleteAsync(long? id = null, CancellationToken ct = default) => _feed.DeleteDeadLettersAsync(id, ct: ct);

    // ── as one of the console's feeds ──────────────────────────────────────────────

    string IWincheFeedConsumers.Feed => StorageFeed.Name;

    Task<IReadOnlyList<FeedDeadLetter>> IWincheFeedConsumers.DeadLettersAsync(string? consumer, int limit, CancellationToken ct) =>
        _feed.GetDeadLettersAsync(consumer, null, limit, ct);

    Task<FeedReplayResult> IWincheFeedConsumers.RetryAsync(string consumer, CancellationToken ct) => RetryAsync(consumer, ct);

    async Task<bool> IWincheFeedConsumers.DiscardAsync(long id, CancellationToken ct) => await DeleteAsync(id, ct) > 0;
}
