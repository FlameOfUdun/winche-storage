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

    public Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null,
        FilePrecondition? precondition = null, FileHeaders? headers = null, CancellationToken ct = default) =>
        Logged("storage.create", path, () => SetCoreAsync(path, mimeType, sizeBytes, metadata, precondition, headers, ct));

    private async Task<FileRecord> SetCoreAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata,
        FilePrecondition? precondition, FileHeaders? headers, CancellationToken ct)
    {
        // create when the path is free, update (seeing the file it replaces) when it isn't; checked in the write's
        // transaction, so the file that was allowed is the one replaced.
        if (inner is Services.FileStorage core)
            return await core.SetAsync(path, mimeType, sizeBytes, metadata, precondition, headers, Check(path), ct);
        var before = await inner.GetAsync(path, ct);
        await AuthorizeAsync(before is null ? RuleOperation.Create : RuleOperation.Update, path, ToResource(before),
            before is null ? "create" : "update", ct, FileToResource.Incoming(path, mimeType, sizeBytes, metadata));
        return await inner.SetAsync(path, mimeType, sizeBytes, metadata, precondition, headers, ct);
    }

    public Task<FileRecord?> GetAsync(string path, CancellationToken ct = default) =>
        Logged("storage.get", path, () => GetCoreAsync(path, ct));

    private async Task<FileRecord?> GetCoreAsync(string path, CancellationToken ct)
    {
        var file = await inner.GetAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Get, path, ToResource(file), "get", ct);
        return file;
    }

    public Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, FilePrecondition? precondition = null,
        FileHeaders? headers = null, CancellationToken ct = default) =>
        Logged("storage.update", path, () => UpdateMetadataCoreAsync(path, patch, precondition, headers, ct));

    private async Task<FileRecord?> UpdateMetadataCoreAsync(string path, JsonObject patch, FilePrecondition? precondition,
        FileHeaders? headers, CancellationToken ct)
    {
        // The rule sees the merged metadata as request.resource, so it can protect keys such as an owner.
        if (inner is Services.FileStorage core)
            return await core.UpdateMetadataAsync(path, patch, precondition, headers, Check(path), ct);
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Update, path, resource, "update", ct);
        return await inner.UpdateMetadataAsync(path, patch, precondition, headers, ct);
    }

    public Task<bool> DeleteAsync(string path, FilePrecondition? precondition = null, CancellationToken ct = default) =>
        Logged("storage.delete", path, () => DeleteCoreAsync(path, precondition, ct));

    private async Task<bool> DeleteCoreAsync(string path, FilePrecondition? precondition, CancellationToken ct)
    {
        // Every file the delete removes (the one at the path and all under it) must be deletable.
        if (inner is Services.FileStorage core)
            return await core.DeleteAsync(path, precondition, Check(path), ct);
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Delete, path, resource, "delete", ct);
        return await inner.DeleteAsync(path, precondition, ct);
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

    public Task<FileListResult> ListAsync(string directory, FileListOptions? options = null, CancellationToken ct = default) =>
        Logged("storage.list", directory, () => ListCoreAsync(directory, options, ct));

    private async Task<FileListResult> ListCoreAsync(string directory, FileListOptions? options, CancellationToken ct)
    {
        await AuthorizeAsync(RuleOperation.List, directory, RuleValues.Null, "list", ct);
        return await inner.ListAsync(directory, options, ct);
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

    /// <summary>The rules check a write runs on each file it changes, inside its transaction.</summary>
    private Services.FileCheck Check(string requested) => (operation, before, after, ct) =>
    {
        var path = before?.Path ?? after?.Path ?? requested;
        var method = operation switch { RuleOperation.Create => "create", RuleOperation.Delete => "delete", _ => "update" };
        return AuthorizeAsync(operation, path, ToResource(before), method, ct, after is null ? null : FileToResource.Convert(after));
    };

    private async Task AuthorizeAsync(RuleOperation operation, string path, Value resource, string method, CancellationToken ct, Value? requestResource = null)
    {
        var request = new RuleRequest
        {
            Resource = resource,
            Request = RuleValues.Request(claimsProvider(), method, requestResource ?? RuleValues.Null),
            Provider = new FileStoreResourceProvider(inner),
        };
        if (!await engine.AllowsAsync(operation, path, request, ct))
        {
            Services.WincheStorageMetrics.AccessDenied.Add(1, new KeyValuePair<string, object?>("operation", method));
            throw new AccessDeniedException(path, method);
        }
    }

    private async Task<Value> FetchResourceAsync(string path, CancellationToken ct)
    {
        var file = await inner.GetAsync(path, ct);
        return ToResource(file);
    }

    private static Value ToResource(FileRecord? file) =>
        file is not null ? FileToResource.Convert(file) : RuleValues.Null;
}
