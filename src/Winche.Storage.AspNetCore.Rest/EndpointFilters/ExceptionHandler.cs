using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Winche.Storage.Authorization;
using Winche.Storage.Models;

namespace Winche.Storage.AspNetCore.Rest.EndpointFilters;

/// <summary>
/// Answers failures with the shared Winche error: <c>{ "error": { code, message, reason? } }</c> and its HTTP status.
/// An unexpected one is logged under a short id the caller gets in <c>details.errorId</c>, and nothing else about it.
/// </summary>
internal class ExceptionHandler(ILogger<ExceptionHandler> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = Map(ex) ?? Unexpected(ex, context.HttpContext);
            return Results.Json(error.ToBody(), statusCode: error.HttpStatus, contentType: "application/json");
        }
    }

    /// <summary>The error for a failure storage expects, or null for an unexpected one.</summary>
    internal static WincheError? Map(Exception ex) => ex switch
    {
        AccessDeniedException => new WincheError(WincheErrorCode.PermissionDenied, ex.Message),
        FileRecordNotFoundException => new WincheError(WincheErrorCode.NotFound, ex.Message),
        FileTooLargeException => new WincheError(WincheErrorCode.InvalidArgument, ex.Message, "too-large"),
        UploadSizeMismatchException => new WincheError(WincheErrorCode.InvalidArgument, ex.Message, "size-mismatch"),
        InvalidUploadStatusException => new WincheError(WincheErrorCode.FailedPrecondition, ex.Message, "upload-status"),
        FileNotUploadedException => new WincheError(WincheErrorCode.FailedPrecondition, ex.Message, "not-uploaded"),
        BadHttpRequestException or ArgumentException or FormatException => new WincheError(WincheErrorCode.InvalidArgument, ex.Message),
        _ => null,
    };

    private WincheError Unexpected(Exception ex, HttpContext http)
    {
        var errorId = Guid.NewGuid().ToString("N")[..12];
        logger.LogError(ex, "Internal error {ErrorId} in {Method} {Path}", errorId, http.Request.Method, http.Request.Path);
        return new WincheError(WincheErrorCode.Internal, $"Internal error ({errorId}).", details: new JsonObject { ["errorId"] = errorId });
    }

    /// <summary>A 404 in the shared shape, for a file that isn't there.</summary>
    internal static IResult NotFound(string path)
    {
        var error = new WincheError(WincheErrorCode.NotFound, $"There's no file at {path}.");
        return Results.Json(error.ToBody(), statusCode: error.HttpStatus, contentType: "application/json");
    }
}
