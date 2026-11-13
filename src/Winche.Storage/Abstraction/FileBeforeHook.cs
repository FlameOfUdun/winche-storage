using System.Text.Json.Nodes;
using Winche.Storage.Models;

namespace Winche.Storage.Abstraction;

/// <summary>What a write does to one file.</summary>
public enum FileOperation { Create, Update, Delete }

/// <summary>
/// Runs before a file write is saved, inside its transaction, after the access rules allowed it: registering a file (new
/// or replacing one), changing its metadata, or deleting it (each file under a deleted directory too). It can refuse the
/// write (throw <see cref="WincheDeniedException"/>, or <see cref="FileWrite.Deny"/>) or change the metadata and headers
/// being saved; never the bytes. It runs for the app's own server-side writes too. Registered per path with
/// <c>o.UseHooks(h => h.Before&lt;THook&gt;("uploads/{uid}/{name}"))</c>. Keep it quick; side effects belong in a
/// <see cref="FileStoreHook"/>, which runs after the commit.
/// </summary>
public abstract class FileBeforeHook
{
    public abstract Task OnBeforeWriteAsync(FileWrite write, CancellationToken ct);
}

/// <summary>One file a write is about to change, as a <see cref="FileBeforeHook"/> sees it.</summary>
public sealed class FileWrite
{
    internal FileWrite(string path, FileOperation operation, FileRecord? before, string? mimeType, long? sizeBytes, JsonObject? metadata,
        string? contentDisposition, string? cacheControl, IReadOnlyDictionary<string, object?>? caller)
    {
        Path = path;
        Operation = operation;
        Before = before;
        MimeType = mimeType;
        SizeBytes = sizeBytes;
        Metadata = metadata;
        ContentDisposition = contentDisposition;
        CacheControl = cacheControl;
        Caller = caller;
    }

    /// <summary>The file's path.</summary>
    public string Path { get; }

    /// <summary>Create (a new file), update (a replacement or a metadata change) or delete.</summary>
    public FileOperation Operation { get; }

    /// <summary>The file as stored now; null for a create.</summary>
    public FileRecord? Before { get; }

    /// <summary>Its declared type and size, as they'll be saved; null for a delete.</summary>
    public string? MimeType { get; }

    public long? SizeBytes { get; }

    /// <summary>The metadata it will have: change it here (it's this write's own copy). Null for a delete.</summary>
    public JsonObject? Metadata { get; }

    /// <summary>The <c>Content-Disposition</c> its downloads will carry (null for none); settable.</summary>
    public string? ContentDisposition { get; set; }

    /// <summary>The <c>Cache-Control</c> its downloads will carry (null for none); settable.</summary>
    public string? CacheControl { get; set; }

    /// <summary>The claims of whoever made the request the write belongs to (<c>uid</c>, …); null when signed out or outside a request.</summary>
    public IReadOnlyDictionary<string, object?>? Caller { get; }

    /// <summary>The caller's uid, when signed in.</summary>
    public string? Uid => Caller?.GetValueOrDefault("uid") as string;

    /// <summary>Refuses the write: the caller gets permissionDenied with <paramref name="message"/>, and nothing is saved.</summary>
    public void Deny(string message) => throw new WincheDeniedException(message);
}

/// <summary>A <see cref="FileBeforeHook"/> bound to a path pattern.</summary>
public sealed record FileBeforeHookRegistration(string Path, FileBeforeHook Hook);

/// <summary>The app's before hooks, run by the write path in registration order.</summary>
internal sealed class FileBeforeHooks(IEnumerable<FileBeforeHookRegistration> registrations, Func<IReadOnlyDictionary<string, object?>?> caller)
{
    private readonly FileBeforeHookRegistration[] _all = [.. registrations];

    public bool Matches(string path) => _all.Any(r => Winche.Rules.Matching.PathMatcher.IsMatch(r.Path, path));

    /// <summary>Runs the hooks for <paramref name="path"/>; null when none matches.</summary>
    public async Task<FileWrite?> RunAsync(string path, FileOperation operation, FileRecord? before, string? mimeType, long? sizeBytes,
        JsonObject? metadata, string? contentDisposition, string? cacheControl, CancellationToken ct)
    {
        if (!Matches(path)) return null;
        var write = new FileWrite(path, operation, before, mimeType, sizeBytes, metadata?.DeepClone().AsObject(), contentDisposition, cacheControl, caller());
        foreach (var r in _all)
            if (Winche.Rules.Matching.PathMatcher.IsMatch(r.Path, path))
                await r.Hook.OnBeforeWriteAsync(write, ct);
        return write;
    }
}
