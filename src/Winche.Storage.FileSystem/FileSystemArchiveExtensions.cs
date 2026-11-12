using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;

namespace Winche.Storage.FileSystem;

public static class FileSystemArchiveExtensions
{
    /// <summary>
    /// Keeps Winche.Storage's files in a folder (<see cref="FileSystemArchiveOptions.Root"/>) instead of S3; map the
    /// upload and download endpoints with <c>app.MapWincheStorageFileSystem()</c>.
    /// </summary>
    public static WincheStorageOptions UseFileSystemArchive(this WincheStorageOptions options, Action<FileSystemArchiveOptions> configure)
    {
        options.Services.Configure(configure);
        options.Services.AddHttpContextAccessor();
        options.Services.TryAddSingleton(TimeProvider.System);
        options.Services.AddSingleton<FileSystemArchive>();
        options.Services.AddSingleton<IArchive>(sp => sp.GetRequiredService<FileSystemArchive>());
        return options;
    }

    /// <summary>
    /// The endpoints <see cref="FileSystemArchive"/>'s signed URLs point at (under <c>/_files</c> by default): PUT and
    /// GET of whole files (with ranges), and PUT of multipart parts. Each checks the URL's signature, expiry and size.
    /// </summary>
    public static IEndpointConventionBuilder MapWincheStorageFileSystem(this IEndpointRouteBuilder app)
    {
        var archive = app.ServiceProvider.GetRequiredService<FileSystemArchive>();
        var types = new FileExtensionContentTypeProvider();
        var group = app.MapGroup(archive.Options.RoutePrefix);

        group.MapPut("/o/{**path}", async (string path, HttpRequest request, CancellationToken ct) =>
        {
            path = Uri.UnescapeDataString(path);
            if (!archive.Verify("PUT", path, request.Query["exp"], request.Query["max"], request.Query["sig"], out var max)) return Results.StatusCode(403);
            if (request.ContentLength > max) return Results.StatusCode(413);
            string file;
            try { file = archive.FileOf(path); }
            catch (ArgumentException) { return Results.BadRequest(); }
            var temp = FileSystemArchive.TempBeside(file);
            try
            {
                if (!await CopyAtMostAsync(request.Body, temp, max, ct)) return Results.StatusCode(413);
                File.Move(temp, file, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            request.HttpContext.Response.Headers.ETag = await FileSystemArchive.ETagAsync(file, ct);
            return Results.Ok();
        }).DisableAntiforgery();

        group.MapGet("/o/{**path}", (string path, HttpRequest request) =>
        {
            path = Uri.UnescapeDataString(path);
            if (!archive.Verify("GET", path, request.Query["exp"], null, request.Query["sig"], out _)) return Results.StatusCode(403);
            string file;
            try { file = archive.FileOf(path); }
            catch (ArgumentException) { return Results.BadRequest(); }
            if (!File.Exists(file)) return Results.NotFound();
            request.HttpContext.Response.Headers.CacheControl = "private, max-age=300";
            return Results.File(file, types.TryGetContentType(file, out var type) ? type : "application/octet-stream", enableRangeProcessing: true);
        });

        group.MapPut("/p/{uploadId}/{part:int}", async (string uploadId, int part, HttpRequest request, HttpResponse response, CancellationToken ct) =>
        {
            if (part is < 1 or > 10_000) return Results.BadRequest();
            if (!archive.Verify("PART", $"{uploadId}/{part}", request.Query["exp"], request.Query["max"], request.Query["sig"], out var max)) return Results.StatusCode(403);
            string dir;
            try { dir = archive.PartsOf(uploadId); }
            catch (ArgumentException) { return Results.BadRequest(); }
            if (!Directory.Exists(dir)) return Results.NotFound();
            var file = Path.Combine(dir, part.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var temp = FileSystemArchive.TempBeside(file);
            try
            {
                if (!await CopyAtMostAsync(request.Body, temp, max, ct)) return Results.StatusCode(413);
                File.Move(temp, file, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            response.Headers.ETag = await FileSystemArchive.ETagAsync(file, ct);
            return Results.Ok();
        }).DisableAntiforgery();

        return group;
    }

    /// <summary>Copies at most <paramref name="max"/> bytes; false (and nothing kept) if the body is longer.</summary>
    private static async Task<bool> CopyAtMostAsync(Stream body, string file, long max, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        await using var output = File.Create(file);
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > max) return false;
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return true;
    }
}
