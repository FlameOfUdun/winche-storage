using Winche.Storage.Models;
using WincheSentinel.Interfaces;
using WincheSentinel.Models;

namespace Winche.Storage.Abstraction;

public abstract class FileAccessRule : IResourceAccessRule<FileRecord>
{
    public abstract string Path { get; }

    public abstract IReadOnlySet<AccessOperation> Operations { get; }

    public abstract Task<bool> EvaluateAsync(AccessContext<FileRecord> context, CancellationToken ct);
}
