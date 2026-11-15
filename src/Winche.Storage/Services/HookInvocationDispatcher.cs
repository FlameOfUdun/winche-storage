using System.Threading.Channels;
using Winche.Rules.Matching;
using Winche.Storage.Abstraction;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.Services;

internal sealed class HookInvocationDispatcher(
    IEnumerable<HookRegistration> hooks
)
{
    private readonly IReadOnlyDictionary<HookRegistration, Channel<HookInvocation>> _channels =
        hooks.ToDictionary(h => h, _ => Channel.CreateUnbounded<HookInvocation>());

    /// <summary>Every registration; file changes are delivered to them durably (<see cref="StorageHookService"/>).</summary>
    internal IReadOnlyCollection<HookRegistration> Registrations => [.. _channels.Keys];

    /// <summary>Tells this node's hook delivery a change just committed, so it needn't wait for its next poll.</summary>
    internal void Wake() => Woken?.Invoke();

    /// <summary>Raised by <see cref="Wake"/>.</summary>
    internal event Action? Woken;

    public IEnumerable<(FileStoreHook Hook, ChannelReader<HookInvocation> Reader)> Readers =>
        _channels.Select(kv => (kv.Key.Hook, kv.Value.Reader));

    /// <summary>
    /// Best-effort, in-memory dispatch — only for the upload/download URL events, which change no data. File
    /// changes go through storage's feed (<see cref="StorageFeed"/>) instead.
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
            channel.Writer.TryComplete();   // stopping twice (e.g. a test host disposed after StopAsync) is fine
    }
}
