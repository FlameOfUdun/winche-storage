using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Winche.Storage.AspNetCore.Abstraction;

namespace Winche.Storage.AspNetCore.Rest.EndpointFilters;

/// <summary>Sets the caller for the request: storage's own MapClaims when it has one, else the host's.</summary>
internal class ClaimsAccessor : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var own = http.RequestServices.GetService<FileClaimsAccessor>();
        WincheCaller.Set(await http.WincheCallerOf(own is null ? null : own.MapClaims));
        return await next(context);
    }
}
