using System.Threading.Channels;
using Winche.Rules.Matching;
using Winche.Storage.Abstraction;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.Services;

public sealed class HookInvocationDispatcher(
    IEnumerable<HookRegistration> hooks
) : IHookInvocationDispatcher
{
    private readonly IReadOnlyDictionary<HookRegistration, Channel<HookInvocation>> _channels =
        hooks.ToDictionary(h => h, _ => Channel.CreateUnbounded<HookInvocation>());

    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Every registration; file changes are delivered to them durably (<see cref="HookDeliveryService"/>).</summary>
    internal IReadOnlyCollection<HookRegistration> Registrations => [.. _channels.Keys];

    /// <summary>Tells this node's delivery worker a change just committed, so it needn't wait for its next poll.</summary>
    internal void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    internal Task<bool> WaitForWakeAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);

    public IEnumerable<(FileStoreHook Hook, ChannelReader<HookInvocation> Reader)> Readers =>
        _channels.Select(kv => (kv.Key.Hook, kv.Value.Reader));

    /// <summary>
    /// Best-effort, in-memory dispatch — only for the upload/download URL events, which change no data. File
    /// changes go through <see cref="HookOutbox"/> instead.
    /// </summary>
    public void Enqueue(string path, Func<FileStoreHook, CancellationToken, Task> invoke)
    {
        foreach (var (registration, channel) in _channels)
        {
            if (!PathMatcher.IsMatch(registration.Path, path)) continue;
            channel.Writer.TryWrite(new HookInvocation(ct => invoke(registration.Hook, ct)));
        }
    }

    public void Complete()
    {
        foreach (var channel in _channels.Values)
            channel.Writer.Complete();
    }
}
