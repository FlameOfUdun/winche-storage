using Winche.Storage.Models;

namespace Winche.Storage.Abstraction;

/// <summary>
/// A file lifecycle hook. The path pattern selecting which files fire this hook is supplied at
/// registration time (<see cref="DependencyInjection.HookBuilder.Add{THook}(string)"/>), not on the
/// hook — so the same hook behavior can be bound to different paths.
/// </summary>
public abstract class FileStoreHook
{
    public virtual Task OnFileRegisteredAsync(FileRecord record, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnUploadConfirmedAsync(FileRecord record, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnFileDeletedAsync(string path, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnMetadataUpdatedAsync(FileRecord record, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnUploadUrlGeneratedAsync(string path, UploadSession session, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnDownloadUrlGeneratedAsync(string path, DownloadSession session, CancellationToken ct) => Task.CompletedTask;
}
