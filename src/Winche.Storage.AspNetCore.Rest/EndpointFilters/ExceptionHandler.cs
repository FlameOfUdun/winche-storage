using Microsoft.AspNetCore.Http;
using Winche.Storage.Models;
using WincheSentinel.Models;

namespace Winche.Storage.AspNetCore.Rest.EndpointFilters;

internal class ExceptionHandler : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidUploadStatusException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400, contentType: "application/json");
        }
        catch (FileNotUploadedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400, contentType: "application/json");
        }
        catch (AccessDeniedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 403, contentType: "application/json");
        }
        catch (FileRecordNotFoundException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 404, contentType: "application/json");
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = "Unexpected error", detail = ex.Message }, statusCode: 500, contentType: "application/json");
        }
    }
}
