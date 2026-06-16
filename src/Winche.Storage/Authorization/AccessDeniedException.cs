namespace Winche.Storage.Authorization;

/// <summary>
/// Thrown by <see cref="RuleGuardedFileStorage"/> when the Winche.Rules engine denies an operation.
/// </summary>
public sealed class AccessDeniedException(string path, string operation)
    : Exception($"Access denied: {operation} on '{path}'")
{
    public string Path { get; } = path;
    public string Operation { get; } = operation;
}
