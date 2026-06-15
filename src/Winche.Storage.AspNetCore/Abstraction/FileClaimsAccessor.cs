using Microsoft.AspNetCore.Http;
using Winche.Storage.Authorization;

namespace Winche.Storage.AspNetCore.Abstraction;

/// <summary>
/// Base class for HTTP-context-based caller claims accessors. Subclasses implement
/// <see cref="MapClaims"/> to extract claims from the current <see cref="HttpContext"/>.
/// Claims are stored per async execution context via <see cref="AsyncLocal{T}"/> so concurrent
/// requests are isolated.
/// </summary>
public abstract class FileClaimsAccessor : IRuleClaimsAccessor
{
    private readonly AsyncLocal<IReadOnlyDictionary<string, object?>?> _asyncLocal = new();

    public abstract IReadOnlyDictionary<string, object?>? MapClaims(HttpContext httpContext);

    public void SetClaims(HttpContext httpContext) => _asyncLocal.Value = MapClaims(httpContext);

    public void SetClaims(IReadOnlyDictionary<string, object?>? claims) => _asyncLocal.Value = claims;

    public IReadOnlyDictionary<string, object?>? GetClaims() => _asyncLocal.Value;
}
