using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Winche.Storage.AspNetCore.Rest.EndpointFilters;
using Winche.Storage.AspNetCore.Rest.Models;
using Winche.Storage.Interfaces;

namespace Winche.Storage.AspNetCore.Rest.DependencyInjection;

public static class WebApplicationExtensions
{
    /// <summary>
    /// Maps the Winche.Storage REST surface under <c>/{prefix}</c>: CRUD via HTTP methods plus the
    /// AIP-136 colon-verbs (<c>:confirm</c>/<c>:upload</c>/<c>:download</c>/<c>:list</c>/<c>:signPart</c>/<c>:listParts</c>).
    /// The <c>{path}</c> segment is base64url-encoded. Returns a single
    /// <see cref="IEndpointConventionBuilder"/> over every endpoint; apply cross-cutting policy on it.
    /// The built-in claims/exception filters are always applied internally and run outermost.
    /// </summary>
    public static IEndpointConventionBuilder MapWincheStorageRestApi(this WebApplication app, string prefix = "files/v1")
    {
        var p = prefix.TrimStart('/');
        var group = app.MapGroup($"/{p}");

        // The first filter added runs outermost: errors from mapping the caller are answered in the shared shape too.
        group.AddEndpointFilter<ExceptionHandler>();
        group.AddEndpointFilter<ClaimsAccessor>();

        group.MapPut("/{path}", async (string path, CreateFileRequest req, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var file = await files.SetAsync(decoded, req.MimeType, req.SizeBytes, req.Metadata, req.Precondition,
                new Winche.Storage.Models.FileHeaders(req.ContentDisposition, req.CacheControl), ct);
            return Results.Json(file);
        });

        group.MapGet("/{path}", async (string path, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var file = await files.GetAsync(decoded, ct);
            return file is null ? ExceptionHandler.NotFound(decoded) : Results.Json(file);
        });

        group.MapPatch("/{path}", async (string path, SetMetadataRequest req, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            if (req.Metadata is null && req.ContentDisposition is null && req.CacheControl is null)
                throw new ArgumentException("Send what to change: { \"metadata\": { key: value or null }, \"contentDisposition\", \"cacheControl\" }.");
            var file = await files.UpdateMetadataAsync(decoded, req.Metadata ?? [], req.Precondition,
                new Winche.Storage.Models.FileHeaders(req.ContentDisposition, req.CacheControl), ct);
            return file is null ? ExceptionHandler.NotFound(decoded) : Results.Json(file);
        });

        group.MapDelete("/{path}", async (string path, [FromQuery] long? ifVersion, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var deleted = await files.DeleteAsync(decoded, ifVersion is { } v ? new Winche.Storage.Models.FilePrecondition(Version: v) : null, ct);
            return deleted ? Results.NoContent() : ExceptionHandler.NotFound(decoded);
        });

        group.MapGet("/ping", () => Results.Ok());

        group.MapPost("/{path}:confirm", async (string path, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var file = await files.ConfirmUploadAsync(decoded, ct);
            return Results.Json(file);
        });

        group.MapPost("/{path}:upload", async (string path, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var session = await files.GenerateUploadUrlAsync(decoded, ct);
            return Results.Json(session);
        });

        group.MapPost("/{path}:download", async (string path, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var session = await files.GenerateDownloadUrlAsync(decoded, ct);
            return Results.Json(session);
        });

        // A folder's files and sub-folders, a page at a time ({items, prefixes, nextPageToken}); /:list is the top.
        group.MapPost("/{path}:list", (string path, [FromQuery] string? mimeType, [FromQuery] int? maxResults, [FromQuery] string? pageToken,
            IFileStorage files, CancellationToken ct) => ListAsync(files, DecodePath(path), mimeType, maxResults, pageToken, ct));
        group.MapPost("/:list", ([FromQuery] string? mimeType, [FromQuery] int? maxResults, [FromQuery] string? pageToken,
            IFileStorage files, CancellationToken ct) => ListAsync(files, "", mimeType, maxResults, pageToken, ct));

        group.MapPost("/{path}:signPart", async (string path, SignPartRequest req, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var session = await files.SignPartAsync(decoded, req.PartNumber, ct);
            return Results.Json(session);
        });

        group.MapPost("/{path}:listParts", async (string path, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var parts = await files.ListUploadedPartsAsync(decoded, ct);
            return Results.Json(parts);
        });

        return group;
    }

    private static async Task<IResult> ListAsync(IFileStorage files, string folder, string? mimeType, int? maxResults, string? pageToken, CancellationToken ct) =>
        Results.Json(await files.ListAsync(folder, new Winche.Storage.Models.FileListOptions(maxResults, pageToken, mimeType), ct));

    /// <summary>Decodes a base64url path segment (no padding, '-'/'_' alphabet) to its UTF-8 string.</summary>
    private static string DecodePath(string encoded) => WincheBase64Url.Decode(encoded);
}
