using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Winche.Storage.AspNetCore.Rest.EndpointFilters;
using Winche.Storage.AspNetCore.Rest.Infrastructure;
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
    public static IEndpointConventionBuilder MapWincheStorageRestApi(this WebApplication app, string prefix = "files")
    {
        var p = prefix.TrimStart('/');
        var group = app.MapGroup($"/{p}");

        group.AddEndpointFilter<ClaimsAccessor>();
        group.AddEndpointFilter<ExceptionHandler>();

        group.MapPut("/{path}", async (string path, CreateFileRequest req, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var file = await files.SetAsync(decoded, req.MimeType, req.SizeBytes, req.Metadata, ct);
            return Results.Json(file);
        });

        group.MapGet("/{path}", async (string path, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var file = await files.GetAsync(decoded, ct);
            return file is null ? Results.NotFound() : Results.Json(file);
        });

        group.MapPatch("/{path}", async (string path, SetMetadataRequest req, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var file = await files.UpdateMetadataAsync(decoded, req.Metadata, ct);
            return file is null ? Results.NotFound() : Results.Json(file);
        });

        group.MapDelete("/{path}", async (string path, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var deleted = await files.DeleteAsync(decoded, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
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

        group.MapPost("/{path}:list", async (string path, [FromQuery] string? mimeType, IFileStorage files, CancellationToken ct) =>
        {
            var decoded = DecodePath(path);
            var records = await files.ListAsync(decoded, mimeType, ct);
            return Results.Json(records);
        });

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

    /// <summary>Decodes a base64url path segment (no padding, '-'/'_' alphabet) to its UTF-8 string.</summary>
    private static string DecodePath(string encoded) => Base64UrlPath.Decode(encoded);
}
