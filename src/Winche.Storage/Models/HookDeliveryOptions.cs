namespace Winche.Storage.Models;

/// <summary>
/// How file hooks (<c>OnFileRegistered</c>, <c>OnUploadConfirmed</c>, <c>OnMetadataUpdated</c>, <c>OnFileDeleted</c>)
/// are delivered. Each file change is written to storage's feed in its own transaction, so none is lost to a restart;
/// each hook consumes the feed on one node at a time, in commit order, at least once (Winche.Core's feed runner).
/// </summary>
public sealed record HookDeliveryOptions
{
    /// <summary>How often to look for changes when not woken by a notification or a local change (default 1 s).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A change a hook keeps failing on is retried with backoff (1 s, doubling, at most 30 s), holding that hook back,
    /// until it has failed this long (default 1 h); then it becomes a dead letter (<c>StorageHooks</c>) and the hook
    /// moves on.
    /// </summary>
    public TimeSpan MaxRetryAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long the feed keeps changes every hook has taken (default 7 days); changes a hook still needs are kept up to
    /// four times as long.
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Changes handed to a hook's consumer at once (default 50).</summary>
    public int BatchSize { get; init; } = 50;
}
