using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Winche.Storage.AspNetCore.Abstraction;
using Winche.Storage.Authorization;
using Winche.Storage.DependencyInjection;

namespace Winche.Storage.AspNetCore.DependencyInjection;

public static class WincheStorageOptionsExtensions
{
    /// <summary>
    /// Registers a delegate-based claims accessor mapping an <see cref="HttpContext"/> to the
    /// caller-claims dictionary consumed by the Winche.Rules guard. The accessor is a singleton
    /// carrying claims per async context via <see cref="System.Threading.AsyncLocal{T}"/>.
    /// </summary>
    public static WincheStorageOptions MapClaims(
        this WincheStorageOptions options,
        Func<HttpContext, IReadOnlyDictionary<string, object?>?> map)
    {
        var accessor = new DelegateFileClaimsAccessor(map);
        options.Services.AddSingleton<FileClaimsAccessor>(accessor);
        options.Services.AddSingleton<IRuleClaimsAccessor>(accessor);
        return options;
    }
}

internal sealed class DelegateFileClaimsAccessor(
    Func<HttpContext, IReadOnlyDictionary<string, object?>?> map) : FileClaimsAccessor
{
    public override IReadOnlyDictionary<string, object?>? MapClaims(HttpContext httpContext) =>
        map(httpContext);
}
