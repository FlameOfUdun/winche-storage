using System.Threading.Channels;
using Winche.Storage.Abstraction;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.Services;

public sealed class HookInvocationDispatcher(
    IEnumerable<FileStoreHook> hooks
) : IHookInvocationDispatcher
{
    private readonly IReadOnlyDictionary<FileStoreHook, Channel<HookInvocation>> _channels =
        hooks.ToDictionary(h => h, _ => Channel.CreateUnbounded<HookInvocation>());

    public IEnumerable<(FileStoreHook Hook, ChannelReader<HookInvocation> Reader)> Readers =>
        _channels.Select(kv => (kv.Key, kv.Value.Reader));

    public void Enqueue(string path, Func<FileStoreHook, CancellationToken, Task> invoke)
    {
        foreach (var (hook, channel) in _channels)
        {
            if (!MatchesPattern(hook.Path, path)) continue;
            channel.Writer.TryWrite(new HookInvocation(ct => invoke(hook, ct)));
        }
    }

    public void Complete()
    {
        foreach (var channel in _channels.Values)
            channel.Writer.Complete();
    }

    // ── Path pattern matching ─────────────────────────────────────────────────
    // Supports literal segments, single-segment captures {name}, and a trailing
    // recursive capture {name=**} (or bare **) that matches zero or more segments.

    private static bool MatchesPattern(string pattern, string path)
    {
        var pSegs = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var dSegs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < pSegs.Length; i++)
        {
            var seg = pSegs[i];

            if (seg == "**" || (seg.StartsWith('{') && seg.EndsWith("=**}")))
                return true;  // recursive wildcard — matches everything from here

            if (i >= dSegs.Length) return false;

            if (seg.StartsWith('{') && seg.EndsWith('}'))
                continue;  // named single-segment capture

            if (!string.Equals(seg, dSegs[i], StringComparison.Ordinal))
                return false;
        }

        return pSegs.Length == dSegs.Length;
    }
}
