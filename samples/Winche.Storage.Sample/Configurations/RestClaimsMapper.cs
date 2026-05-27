using Microsoft.AspNetCore.Http;
using Winche.Storage.AspNetCore.Rest.Abstraction;

namespace Winche.Storage.Sample.Configurations;

public class UserClaimsMapper : FileClaimsAccessor
{
    public override IReadOnlyDictionary<string, object?> MapClaims(HttpContext httpContext)
    {
        return new Dictionary<string, object?>
        {
            ["userId"] = httpContext.Request.Headers.TryGetValue("X-USER-ID", out var userId) ? userId.ToString() : "",
        };
    }
}
