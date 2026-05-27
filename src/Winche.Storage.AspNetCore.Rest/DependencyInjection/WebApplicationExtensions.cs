using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using System.Text;
using Winche.Storage.AspNetCore.Rest.EndpointFilters;
using Winche.Storage.AspNetCore.Rest.Models;
using Winche.Storage.Interfaces;

namespace Winche.Storage.AspNetCore.Rest.DependencyInjection
{
    public static class WebApplicationExtensions
    {
        private static string DecodeBase64(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }

        public static WebApplication UseWincheStorageRestApi(this WebApplication app, string prefix = "files", Action<RouteGroupBuilder>? configure = null)
        {
            var group = app.MapGroup(prefix);

            configure?.Invoke(group);

            group.AddEndpointFilter<CallerAccessor>();
            group.AddEndpointFilter<ExceptionHandler>();

            group.MapPost("/{path}", async (string path, CreateFileRequest req, IFileManager files, CancellationToken ct) =>
            {
                var decoded = DecodeBase64(path);
                var file = await files.SetAsync(decoded, req.MimeType, req.SizeBytes, req.Metadata, ct);
                return Results.Json(file);
            });

            group.MapGet("/{path}", async (string path, IFileManager files, CancellationToken ct) =>
            {
                var decoded = DecodeBase64(path);
                var file = await files.GetAsync(decoded, ct);
                return file is null ? Results.NotFound() : Results.Json(file);
            });

            group.MapDelete("/{path}", async (string path, IFileManager files, CancellationToken ct) =>
            {
                var decoded = DecodeBase64(path);
                var deleted = await files.DeleteAsync(decoded, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            });

            group.MapPut("/{path}/metadata", async (string path, SetMetadataRequest req, IFileManager files, CancellationToken ct) =>
            {
                var decoded = DecodeBase64(path);
                var file = await files.UpdateMetadataAsync(decoded, req.Metadata, ct);
                return file is null ? Results.NotFound() : Results.Json(file);
            });

            group.MapPost("/{path}/confirm", async (string path, IFileManager files, CancellationToken ct) =>
            {
                var decoded = DecodeBase64(path);
                var file = await files.ConfirmUploadAsync(decoded, ct);
                return Results.Json(file);
            });

            group.MapGet("/{path}/download", async (string path, IFileManager files, CancellationToken ct) =>
            {
                var decoded = DecodeBase64(path);
                var file = await files.GenerateDownloadUrlAsync(decoded, ct);
                return Results.Json(file);
            });

            group.MapGet("/{path}/upload", async (string path, IFileManager files, CancellationToken ct) =>
            {
                var decoded = DecodeBase64(path);
                var file = await files.GenerateUploadUrlAsync(decoded, ct);
                return Results.Json(file);
            });

            group.MapGet("/{path}/list", async (string path, [FromQuery] string? mimeType, IFileManager files, CancellationToken ct) =>
            {
                var decoded = DecodeBase64(path);
                var records = await files.ListAsync(decoded, mimeType, ct);
                return Results.Json(records);
            });

            group.MapGet("/{path}/upload/parts/{partNumber:int}", async (string path, int partNumber, IFileManager files, CancellationToken ct) =>
            {
                var decoded = DecodeBase64(path);
                var session = await files.SignPartAsync(decoded, partNumber, ct);
                return Results.Json(session);
            });

            group.MapGet("/{path}/parts", async (string path, IFileManager files, CancellationToken ct) =>
            {
                var decoded = DecodeBase64(path);
                var parts = await files.ListUploadedPartsAsync(decoded, ct);
                return Results.Json(parts);
            });

            group.MapGet("/ping", async (CancellationToken ct) =>
            {
                return Results.Ok();
            });

            return app;
        }
    }
}