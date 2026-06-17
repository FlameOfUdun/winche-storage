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

    public IEnumerable<(FileStoreHook Hook, ChannelReader<HookInvocation> Reader)> Readers =>
        _channels.Select(kv => (kv.Key.Hook, kv.Value.Reader));

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
