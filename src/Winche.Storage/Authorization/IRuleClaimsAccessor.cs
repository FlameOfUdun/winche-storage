namespace Winche.Storage.Authorization;

/// <summary>
/// Provides caller claims for the Winche.Rules-based authorization guard
/// (<see cref="RuleGuardedFileStorage"/>).
/// Returns <see langword="null"/> when no authenticated caller is present.
/// </summary>
public interface IRuleClaimsAccessor
{
    IReadOnlyDictionary<string, object?>? GetClaims();
}
