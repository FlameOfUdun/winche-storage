using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;
using Winche.Values;

namespace Winche.Storage.Authorization;

/// <summary>
/// <see cref="RuleEngine"/>-based authorization decorator over the unguarded <see cref="Services.FileStorage"/> core.
/// Every operation authorizes via the engine before delegating to the inner core. For trusted server-side
/// callers that must bypass the rules engine, resolve the concrete <see cref="Services.FileStorage"/> instead.
/// </summary>
public sealed class RuleGuardedFileStorage(
    IFileStorage inner,
    RuleEngine engine,
    Func<IReadOnlyDictionary<string, object?>?> claimsProvider)
    : IFileStorage
{
    private Logging.RequestLog? _log;

    /// <summary>As above, logging each request (<see cref="DependencyInjection.WincheStorageOptions.LogRequests"/>).</summary>
    internal RuleGuardedFileStorage(IFileStorage inner, RuleEngine engine, Func<IReadOnlyDictionary<string, object?>?> claimsProvider,
        Logging.RequestLog? log) : this(inner, engine, claimsProvider) => _log = log;

    private Task<T> Logged<T>(string operation, string path, Func<Task<T>> body) =>
        _log is null ? body() : _log.RunAsync(operation, path, body);

    // ── Protected operations ──────────────────────────────────────────────────

    public Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null, CancellationToken ct = default) =>
        Logged("storage.create", path, () => SetCoreAsync(path, mimeType, sizeBytes, metadata, ct));

    private async Task<FileRecord> SetCoreAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata, CancellationToken ct)
    {
        // The file being registered is request.resource, so a create rule can check its size or type.
        var incoming = FileToResource.Incoming(path, mimeType, sizeBytes, metadata);
        await AuthorizeAsync(RuleOperation.Create, path, RuleValues.Null, "create", ct, incoming);
        return await inner.SetAsync(path, mimeType, sizeBytes, metadata, ct);
    }

    public Task<FileRecord?> GetAsync(string path, CancellationToken ct = default) =>
        Logged("storage.get", path, () => GetCoreAsync(path, ct));

    private async Task<FileRecord?> GetCoreAsync(string path, CancellationToken ct)
    {
        var file = await inner.GetAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Get, path, ToResource(file), "get", ct);
        return file;
    }

    public Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, CancellationToken ct = default) =>
        Logged("storage.update", path, () => UpdateMetadataCoreAsync(path, patch, ct));

    private async Task<FileRecord?> UpdateMetadataCoreAsync(string path, JsonObject patch, CancellationToken ct)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Update, path, resource, "update", ct);
        return await inner.UpdateMetadataAsync(path, patch, ct);
    }

    public Task<bool> DeleteAsync(string path, CancellationToken ct = default) =>
        Logged("storage.delete", path, () => DeleteCoreAsync(path, ct));

    private async Task<bool> DeleteCoreAsync(string path, CancellationToken ct)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Delete, path, resource, "delete", ct);
        return await inner.DeleteAsync(path, ct);
    }

    public Task<UploadSession> GenerateUploadUrlAsync(string path, CancellationToken ct = default) =>
        Logged("storage.upload", path, () => GenerateUploadUrlCoreAsync(path, ct));

    private async Task<UploadSession> GenerateUploadUrlCoreAsync(string path, CancellationToken ct)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Update, path, resource, "update", ct);
        return await inner.GenerateUploadUrlAsync(path, ct);
    }

    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default) =>
        Logged("storage.download", path, () => GenerateDownloadUrlCoreAsync(path, ct));

    private async Task<DownloadSession> GenerateDownloadUrlCoreAsync(string path, CancellationToken ct)
    {
        var file = await inner.GetAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Get, path, ToResource(file), "get", ct);
        return await inner.GenerateDownloadUrlAsync(path, ct);
    }

    public Task<FileRecord> ConfirmUploadAsync(string path, CancellationToken ct = default) =>
        Logged("storage.confirm", path, () => ConfirmUploadCoreAsync(path, ct));

    private async Task<FileRecord> ConfirmUploadCoreAsync(string path, CancellationToken ct)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Update, path, resource, "update", ct);
        return await inner.ConfirmUploadAsync(path, ct);
    }

    public Task<IEnumerable<FileRecord>> ListAsync(string directory, string? mimeType = null, CancellationToken ct = default) =>
        Logged("storage.list", directory, () => ListCoreAsync(directory, mimeType, ct));

    private async Task<IEnumerable<FileRecord>> ListCoreAsync(string directory, string? mimeType, CancellationToken ct)
    {
        await AuthorizeAsync(RuleOperation.List, directory, RuleValues.Null, "list", ct);
        return await inner.ListAsync(directory, mimeType, ct);
    }

    public Task<UploadSession> SignPartAsync(string path, int partNumber, CancellationToken ct = default) =>
        Logged("storage.uploadPart", path, () => SignPartCoreAsync(path, partNumber, ct));

    private async Task<UploadSession> SignPartCoreAsync(string path, int partNumber, CancellationToken ct)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Update, path, resource, "update", ct);
        return await inner.SignPartAsync(path, partNumber, ct);
    }

    public Task<IEnumerable<FilePart>> ListUploadedPartsAsync(string path, CancellationToken ct = default) =>
        Logged("storage.parts", path, () => ListUploadedPartsCoreAsync(path, ct));

    private async Task<IEnumerable<FilePart>> ListUploadedPartsCoreAsync(string path, CancellationToken ct)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Get, path, resource, "get", ct);
        return await inner.ListUploadedPartsAsync(path, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task AuthorizeAsync(RuleOperation operation, string path, Value resource, string method, CancellationToken ct, Value? requestResource = null)
    {
        var request = new RuleRequest
        {
            Resource = resource,
            Request = RuleValues.Request(claimsProvider(), method, requestResource ?? RuleValues.Null),
            Provider = new FileStoreResourceProvider(inner),
        };
        if (!await engine.AllowsAsync(operation, path, request, ct))
            throw new AccessDeniedException(path, method);
    }

    private async Task<Value> FetchResourceAsync(string path, CancellationToken ct)
    {
        var file = await inner.GetAsync(path, ct);
        return ToResource(file);
    }

    private static Value ToResource(FileRecord? file) =>
        file is not null ? FileToResource.Convert(file) : RuleValues.Null;
}
