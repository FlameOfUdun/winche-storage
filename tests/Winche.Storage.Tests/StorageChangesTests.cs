using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Models;
using Xunit;

namespace Winche.Storage.Tests;

public class StorageChangesTests
{
    private sealed class Sink : IWincheChangeSink
    {
        public List<WincheChange> Changes { get; } = [];
        public Task OnChangesAsync(IReadOnlyList<WincheChange> changes, CancellationToken ct)
        {
            Changes.AddRange(changes);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task File_changes_reach_every_sink_with_their_variables()
    {
        var sink = new Sink();
        var publisher = new ChangePublisher([sink], TimeProvider.System);
        var record = new FileRecord
        {
            Id = "f1", Directory = "users/u1", Path = "users/u1/a.png", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            MetaData = [], Version = 1, MimeType = "image/png", SizeBytes = 3, UploadStatus = UploadStatus.Complete,
        };
        await publisher.OnUploadConfirmedAsync(record, default);
        await publisher.OnFileDeletedAsync("users/u1/a.png", default);

        Assert.Equal(["uploaded", "deleted"], sink.Changes.Select(c => c.Type));
        var uploaded = sink.Changes[0];
        Assert.Equal(("storage", "users/u1/a.png"), (uploaded.Source, uploaded.Path));
        Assert.Equal(("a.png", "users/u1"), (uploaded.Variables["name"], uploaded.Variables["directory"]));
        Assert.Equal("image/png", (string?)uploaded.Data!["mimeType"]);
        Assert.Null(sink.Changes[1].Data);
    }

    [Fact]
    public void Storage_offers_its_action_and_PublishChanges_registers_its_source()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWincheStorage(o => { o.ConnectionString = "Host=localhost"; o.UseHooks(h => h.PublishChanges()); });
        using var provider = services.BuildServiceProvider();
        Assert.Contains(provider.GetServices<IWincheAction>(), a => a.Name == "storage.deleteFiles");
        Assert.Contains(provider.GetServices<WincheChangeSource>(), s => s.Name == "storage");
    }
}
