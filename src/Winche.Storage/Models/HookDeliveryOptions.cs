namespace Winche.Storage.Models;

/// <summary>
/// How file hooks (<c>OnFileRegistered</c>, <c>OnUploadConfirmed</c>, <c>OnMetadataUpdated</c>, <c>OnFileDeleted</c>)
/// are delivered. Each delivery is written to Postgres in the same transaction as the file change, so none is lost
/// to a restart; every node runs deliveries and each runs on exactly one node.
/// </summary>
public sealed record HookDeliveryOptions
{
    /// <summary>How often to look for due deliveries when not woken by a local change (default 1 s).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a node holds a delivery while its hook runs (default 5 min). A delivery whose node dies is
    /// retried once this passes; a hook that runs longer may be called again on another node.
    /// </summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A failing delivery is retried with backoff (1 s, doubling, at most <see cref="MaxBackoff"/>) until it is
    /// this old (default 1 h); then it becomes a dead letter (<c>StorageHookDeadLetters</c>).
    /// </summary>
    public TimeSpan MaxRetryAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Longest wait between retries (default 5 min).</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Dead letters, and deliveries for hooks no node registers any more, are deleted once this old (default 7 days).
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Deliveries claimed per round (default 50).</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>Hooks run at once on a node (default 8).</summary>
    public int MaxParallel { get; init; } = 8;
}
