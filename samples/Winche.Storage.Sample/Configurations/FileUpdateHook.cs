using Winche.Storage.Abstraction;
using Winche.Storage.Models;

namespace Winche.Storage.Sample.Configurations;

public class FileUpdateHook : FileStoreHook
{
    public override string Path => "userFiles/{userId}/**";

    public override Task OnFileRegisteredAsync(FileRecord record, CancellationToken ct)
    {
        Console.WriteLine($"File registered: {record.Path}");
        return Task.CompletedTask;
    }

    public override Task OnMetadataUpdatedAsync(FileRecord record, CancellationToken ct)
    {
        Console.WriteLine($"Metadata updated: {record.Path}");
        return Task.CompletedTask;
    }

    public override Task OnFileDeletedAsync(string path, CancellationToken ct)
    {
        Console.WriteLine($"File deleted: {path}");
        return Task.CompletedTask;
    }

    public override Task OnUploadUrlGeneratedAsync(string path, UploadSession session, CancellationToken ct)
    {
        Console.WriteLine($"Upload URL generated for {path}: {session.Url}");
        return Task.CompletedTask;
    }

    public override Task OnDownloadUrlGeneratedAsync(string path, DownloadSession session, CancellationToken ct)
    {
        Console.WriteLine($"Download URL generated for {path}: {session.Url}");
        return Task.CompletedTask;
    }

    public override Task OnUploadConfirmedAsync(FileRecord record, CancellationToken ct)
    {
        Console.WriteLine($"Upload confirmed for {record.Path}");
        return Task.CompletedTask;
    }
}
