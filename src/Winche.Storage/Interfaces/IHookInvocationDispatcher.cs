using System.Threading.Channels;
using Winche.Storage.Abstraction;
using Winche.Storage.Models;

namespace Winche.Storage.Interfaces;

public interface IHookInvocationDispatcher
{
    IEnumerable<(FileStoreHook Hook, ChannelReader<HookInvocation> Reader)> Readers { get; }
    void Enqueue(string path, Func<FileStoreHook, CancellationToken, Task> invoke);
    void Complete();
}
