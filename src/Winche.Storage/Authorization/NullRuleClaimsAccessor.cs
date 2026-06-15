namespace Winche.Storage.Authorization;

/// <summary>
/// Fallback <see cref="IRuleClaimsAccessor"/> that always returns <see langword="null"/>,
/// representing an unauthenticated caller. Registered first by <c>AddWincheStorage</c>;
/// transport packages override it by adding a later registration via <c>MapClaims(...)</c>.
/// </summary>
internal sealed class NullRuleClaimsAccessor : IRuleClaimsAccessor
{
    public static readonly NullRuleClaimsAccessor Instance = new();

    public IReadOnlyDictionary<string, object?>? GetClaims() => null;
}
