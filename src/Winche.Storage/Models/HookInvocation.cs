namespace Winche.Storage.Models;

public sealed record HookInvocation(Func<CancellationToken, Task> Invoke);
