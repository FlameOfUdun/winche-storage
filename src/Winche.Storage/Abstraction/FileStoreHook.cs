using Winche.Storage.Models;

namespace Winche.Storage.Abstraction;

public abstract class FileStoreHook
{
    public abstract string Path { get; }

    public virtual Task OnFileRegisteredAsync(FileRecord record, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnUploadConfirmedAsync(FileRecord record, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnFileDeletedAsync(string path, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnMetadataUpdatedAsync(FileRecord record, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnUploadUrlGeneratedAsync(string path, UploadSession session, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnDownloadUrlGeneratedAsync(string path, DownloadSession session, CancellationToken ct) => Task.CompletedTask;
}
