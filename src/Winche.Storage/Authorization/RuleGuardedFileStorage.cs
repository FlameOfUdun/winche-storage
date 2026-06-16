using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.Authorization;

/// <summary>
/// <see cref="RuleEngine"/>-based authorization decorator over the unguarded <see cref="IFileStorage"/> core.
/// Protected operations authorize via the engine before delegating to the inner storage's unprotected counterparts.
/// Unprotected operations delegate directly, bypassing the rules engine.
/// </summary>
public sealed class RuleGuardedFileStorage(
    IFileStorage inner,
    RuleEngine engine,
    Func<IReadOnlyDictionary<string, object?>?> claimsProvider)
    : IFileStorage
{
    // ── Protected operations ──────────────────────────────────────────────────

    public async Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null, CancellationToken ct = default)
    {
        await AuthorizeAsync(RuleOperation.Create, path, RuleValue.Null, "create", ct);
        return await inner.SetUnprotectedAsync(path, mimeType, sizeBytes, metadata, ct);
    }

    public async Task<FileRecord?> GetAsync(string path, CancellationToken ct = default)
    {
        var file = await inner.GetUnprotectedAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Get, path, ToResource(file), "get", ct);
        return file;
    }

    public async Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, CancellationToken ct = default)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Update, path, resource, "update", ct);
        return await inner.UpdateUnprotectedAsync(path, patch, ct);
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Delete, path, resource, "delete", ct);
        return await inner.DeleteUnprotectedAsync(path, ct);
    }

    public async Task<UploadSession> GenerateUploadUrlAsync(string path, CancellationToken ct = default)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Update, path, resource, "update", ct);
        return await inner.GenerateUploadUrlUnprotectedAsync(path, ct);
    }

    public async Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default)
    {
        var file = await inner.GetUnprotectedAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Get, path, ToResource(file), "get", ct);
        return await inner.GenerateDownloadUrlUnprotectedAsync(path, ct);
    }

    public async Task<FileRecord> ConfirmUploadAsync(string path, CancellationToken ct = default)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Update, path, resource, "update", ct);
        return await inner.ConfirmUploadUnprotectedAsync(path, ct);
    }

    public async Task<IEnumerable<FileRecord>> ListAsync(string directory, string? mimeType = null, CancellationToken ct = default)
    {
        await AuthorizeAsync(RuleOperation.List, directory, RuleValue.Null, "list", ct);
        return await inner.ListUnprotectedAsync(directory, mimeType, ct);
    }

    public async Task<UploadSession> SignPartAsync(string path, int partNumber, CancellationToken ct = default)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Update, path, resource, "update", ct);
        return await inner.SignPartAsync(path, partNumber, ct);
    }

    public async Task<IEnumerable<FilePart>> ListUploadedPartsAsync(string path, CancellationToken ct = default)
    {
        var resource = await FetchResourceAsync(path, ct);
        await AuthorizeAsync(RuleOperation.Get, path, resource, "get", ct);
        return await inner.ListUploadedPartsAsync(path, ct);
    }

    // ── Unprotected pass-throughs ─────────────────────────────────────────────

    public Task<FileRecord> SetUnprotectedAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null, CancellationToken ct = default) =>
        inner.SetUnprotectedAsync(path, mimeType, sizeBytes, metadata, ct);

    public Task<FileRecord?> GetUnprotectedAsync(string path, CancellationToken ct = default) =>
        inner.GetUnprotectedAsync(path, ct);

    public Task<FileRecord?> UpdateUnprotectedAsync(string path, JsonObject patch, CancellationToken ct = default) =>
        inner.UpdateUnprotectedAsync(path, patch, ct);

    public Task<bool> DeleteUnprotectedAsync(string path, CancellationToken ct = default) =>
        inner.DeleteUnprotectedAsync(path, ct);

    public Task<UploadSession> GenerateUploadUrlUnprotectedAsync(string path, CancellationToken ct = default) =>
        inner.GenerateUploadUrlUnprotectedAsync(path, ct);

    public Task<DownloadSession> GenerateDownloadUrlUnprotectedAsync(string path, CancellationToken ct = default) =>
        inner.GenerateDownloadUrlUnprotectedAsync(path, ct);

    public Task<FileRecord> ConfirmUploadUnprotectedAsync(string path, CancellationToken ct = default) =>
        inner.ConfirmUploadUnprotectedAsync(path, ct);

    public Task<IEnumerable<FileRecord>> ListUnprotectedAsync(string directory, string? mimeType = null, CancellationToken ct = default) =>
        inner.ListUnprotectedAsync(directory, mimeType, ct);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task AuthorizeAsync(RuleOperation operation, string path, RuleValue resource, string method, CancellationToken ct)
    {
        var request = new RuleRequest
        {
            Resource = resource,
            Request = RequestBuilder.Build(claimsProvider(), method, RuleValue.Null),
            Provider = new FileStoreResourceProvider(inner),
        };
        if (!await engine.AllowsAsync(operation, path, request, ct))
            throw new AccessDeniedException(path, method);
    }

    private async Task<RuleValue> FetchResourceAsync(string path, CancellationToken ct)
    {
        var file = await inner.GetUnprotectedAsync(path, ct);
        return ToResource(file);
    }

    private static RuleValue ToResource(FileRecord? file) =>
        file is not null ? FileToResource.Convert(file) : RuleValue.Null;
}
