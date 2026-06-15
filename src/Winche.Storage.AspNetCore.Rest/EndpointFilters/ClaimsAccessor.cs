using Microsoft.AspNetCore.Http;
using Winche.Storage.AspNetCore.Abstraction;

namespace Winche.Storage.AspNetCore.Rest.EndpointFilters;

internal class ClaimsAccessor(FileClaimsAccessor accessor) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        accessor.SetClaims(context.HttpContext);
        return await next(context);
    }
}
