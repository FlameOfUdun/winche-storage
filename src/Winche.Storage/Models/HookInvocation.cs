namespace Winche.Storage.Models;

internal sealed record HookInvocation(Func<CancellationToken, Task> Invoke);
