using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Winche.Storage.Authorization;
using Winche.Storage.Models;

namespace Winche.Storage.AspNetCore.Rest.EndpointFilters;

/// <summary>
/// Answers failures with the shared Winche error: <c>{ "error": { code, message, reason? } }</c> and its HTTP status.
/// An unexpected one is logged under a short id the caller gets in <c>details.errorId</c>, and nothing else about it.
/// </summary>
internal class ExceptionHandler(ILogger<ExceptionHandler> logger) : WincheErrorFilter(logger)
{
    protected override WincheError? Map(Exception exception) => Known(exception);

    /// <summary>The error for a failure storage expects, or null for an unexpected one.</summary>
    internal static WincheError? Known(Exception ex) => ex switch
    {
        StorageException se => se.Error,
        AccessDeniedException => new WincheError(WincheErrorCode.PermissionDenied, ex.Message),
        FileRecordNotFoundException => new WincheError(WincheErrorCode.NotFound, ex.Message),
        FileTooLargeException => new WincheError(WincheErrorCode.InvalidArgument, ex.Message, "too-large"),
        UploadSizeMismatchException => new WincheError(WincheErrorCode.InvalidArgument, ex.Message, "size-mismatch"),
        InvalidUploadStatusException => new WincheError(WincheErrorCode.FailedPrecondition, ex.Message, "upload-status"),
        FileNotUploadedException => new WincheError(WincheErrorCode.FailedPrecondition, ex.Message, "not-uploaded"),
        ArgumentException or FormatException => new WincheError(WincheErrorCode.InvalidArgument, ex.Message),
        _ => null,
    };

    /// <summary>A 404 in the shared shape, for a file that isn't there.</summary>
    internal static IResult NotFound(string path) => new WincheError(WincheErrorCode.NotFound, $"There's no file at {path}.").ToResult();
}
