using System.Threading.Channels;
using Winche.Storage.Abstraction;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;
using Winche.Sentinel.Interfaces;

namespace Winche.Storage.Services;

public sealed class HookInvocationDispatcher(
    IEnumerable<FileStoreHook> hooks,
    IPathPatternMatcher<FileRecord> matcher
) : IHookInvocationDispatcher
{
    private readonly IReadOnlyDictionary<FileStoreHook, Channel<HookInvocation>> _channels = hooks.ToDictionary(h => h, _ => Channel.CreateUnbounded<HookInvocation>());

    public IEnumerable<(FileStoreHook Hook, ChannelReader<HookInvocation> Reader)> Readers => _channels.Select(kv => (kv.Key, kv.Value.Reader));

    public void Enqueue(string path, Func<FileStoreHook, CancellationToken, Task> invoke)
    {
        foreach (var (hook, channel) in _channels)
        {
            var result = matcher.Match(hook.Path, path);
            if (!result.IsMatch) continue;
            channel.Writer.TryWrite(new HookInvocation(ct => invoke(hook, ct)));
        }
    }

    public void Complete()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Writer.Complete();
        }
    }
}
