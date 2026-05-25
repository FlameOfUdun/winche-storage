using Microsoft.AspNetCore.Http;

namespace Winche.Storage.AspNetCore.Rest.Abstraction;

public abstract class RestClaimsMapper
{
    public abstract Task<Dictionary<string, object?>> MapClaims(HttpContext httpContext);
}
