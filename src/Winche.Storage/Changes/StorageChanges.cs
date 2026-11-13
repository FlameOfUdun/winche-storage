using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Winche.Storage.Abstraction;
using Winche.Storage.Models;
using Winche.Storage.Services;

namespace Winche.Storage.DependencyInjection;

/// <summary>Publishing file changes to the app's change sinks (Winche.Webhooks, Winche.Functions).</summary>
public static class StorageChangesExtensions
{
    /// <summary>Storage's changes, as hooks and functions see them.</summary>
    public static readonly WincheChangeSource Source = new("storage", "Files", ["uploaded", "deleted", "metadataUpdated"], HasPaths: true,
        Variables: ["path", "name", "directory"]);

    /// <summary>
    /// Publishes file changes (uploaded, deleted, metadata updated) to the app's <see cref="IWincheChangeSink"/>s,
    /// inside <c>AddWincheStorage(o =&gt; o.UseHooks(h =&gt; h.PublishChanges()))</c>. Delivered durably by storage.
    /// </summary>
    public static HookBuilder PublishChanges(this HookBuilder hooks)
    {
        hooks.Add<ChangePublisher>("{file=**}");
        hooks.Services.AddSingleton(Source);
        hooks.Services.TryAddSingleton(TimeProvider.System);
        return hooks;
    }

    /// <summary>Storage's ready-made actions (<c>storage.deleteFiles</c>), registered with storage.</summary>
    internal static void AddStorageActions(IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWincheAction, DeleteFilesAction>());
}

/// <summary>Turns storage's hook calls (delivered durably) into <see cref="WincheChange"/>s for every sink.</summary>
internal sealed class ChangePublisher(IEnumerable<IWincheChangeSink> sinks, TimeProvider time) : FileStoreHook
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Task Publish(string type, string path, FileRecord? record, CancellationToken ct)
    {
        var slash = path.LastIndexOf('/');
        var vars = new Dictionary<string, string> { ["path"] = path, ["name"] = path[(slash + 1)..], ["directory"] = slash < 0 ? "" : path[..slash] };
        var data = record is null ? null : JsonSerializer.SerializeToNode(record, Json) as JsonObject;
        return WincheChanges.PublishAsync(sinks, new WincheChange("storage", type, path, time.GetUtcNow(), data, null, vars), ct);
    }

    public override Task OnUploadConfirmedAsync(FileRecord record, CancellationToken ct) => Publish("uploaded", record.Path, record, ct);
    public override Task OnFileDeletedAsync(string path, CancellationToken ct) => Publish("deleted", path, null, ct);
    public override Task OnMetadataUpdatedAsync(FileRecord record, CancellationToken ct) => Publish("metadataUpdated", record.Path, record, ct);
}

/// <summary>Ready-made: delete every file under a folder (e.g. a user's uploads when their account goes).</summary>
internal sealed class DeleteFilesAction : IWincheAction
{
    public string Name => "storage.deleteFiles";
    public string Title => "Delete files";
    public string Description => "Deletes every file under a folder. Running it again is harmless.";
    public IReadOnlyList<string> Sources => [];
    public IReadOnlyList<WincheActionOption> Options { get; } =
        [new("folder", "Folder to empty", "users/{uid}/", Help: "Every file whose path starts with this. {name} is filled from the trigger.")];

    public async Task<string> RunAsync(WincheActionContext context, CancellationToken ct)
    {
        var folder = context.Options["folder"].TrimStart('/');
        if (!folder.EndsWith('/')) folder += "/";
        var deleted = await context.Services.GetRequiredService<FileStorage>().DeleteUnderAsync(folder, ct);
        return deleted == 0 ? $"No files under {folder}." : $"Deleted {deleted} file{(deleted == 1 ? "" : "s")} under {folder}.";
    }
}
