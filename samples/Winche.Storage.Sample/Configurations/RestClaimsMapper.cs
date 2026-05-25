using Winche.Storage.AspNetCore.Rest.Abstraction;

namespace Winche.Storage.Sample.Configurations;

public class UserClaimsMapper : RestClaimsMapper
{
    public override Task<Dictionary<string, object?>> MapClaims(HttpContext httpContext)
    {
        return Task.FromResult(new Dictionary<string, object?>
        {
            ["userId"] = httpContext.Request.Headers.TryGetValue("X-USER-ID", out var userId) ? userId.ToString() : "",
        });
    }
}
