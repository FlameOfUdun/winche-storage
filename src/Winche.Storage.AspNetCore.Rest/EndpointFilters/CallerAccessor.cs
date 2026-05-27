using Microsoft.AspNetCore.Http;
using Winche.Storage.AspNetCore.Rest.Abstraction;

namespace Winche.Storage.AspNetCore.Rest.EndpointFilters;

internal class CallerAccessor(FileClaimsAccessor accessor) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        accessor.SetClaims(context.HttpContext);
        return await next(context);
    }
}
