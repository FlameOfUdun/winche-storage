using Winche.Storage.Models;

namespace Winche.Storage.Interfaces;

internal interface IHookInvocationProcessor
{
     Task ConsumeAsync(System.Threading.Channels.ChannelReader<HookInvocation> reader, CancellationToken ct);
}
