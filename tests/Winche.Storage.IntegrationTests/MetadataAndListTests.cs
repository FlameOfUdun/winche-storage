using System.Text.Json.Nodes;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

public class MetadataAndListTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private FileStorage NewStorage() =>
        new(fixture.DataSource, new FakeArchive(), new HookInvocationDispatcher([]));

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpdateMetadataAsync_replaces_the_whole_document()
    {
        var storage = NewStorage();
        await storage.SetAsync("docs/a.txt", "text/plain", 1, new JsonObject { ["a"] = 1 });

        var updated = await storage.UpdateMetadataAsync("docs/a.txt", new JsonObject { ["b"] = 2 });

        Assert.NotNull(updated);
        Assert.Equal(2, (int)updated!.MetaData["b"]!);
        Assert.Null(updated.MetaData["a"]); // replaced, not merged
    }

    [Fact]
    public async Task UpdateMetadataAsync_returns_null_for_a_missing_path()
    {
        var storage = NewStorage();
        var result = await storage.UpdateMetadataAsync("nope.txt", new JsonObject { ["x"] = 1 });
        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_scopes_to_directory_and_filters_by_mime()
    {
        var storage = NewStorage();
        await storage.SetAsync("a/x.txt", "text/plain", 1);
        await storage.SetAsync("a/y.json", "application/json", 1);
        await storage.SetAsync("b/z.txt", "text/plain", 1);

        var inA = await storage.ListAsync("a");
        var jsonInA = await storage.ListAsync("a", "application/json");

        Assert.Equal(2, inA.Count());
        Assert.Equal(["a/y.json"], jsonInA.Select(r => r.Path));
    }

    [Fact]
    public async Task ListDirectoryIdsAsync_paginates_with_a_continuation_token()
    {
        var storage = NewStorage();
        await storage.SetAsync("d1/f.txt", "text/plain", 1);
        await storage.SetAsync("d2/f.txt", "text/plain", 1);
        await storage.SetAsync("d3/f.txt", "text/plain", 1);

        var page1 = await storage.ListDirectoryIdsAsync(parentDirectory: null, pageSize: 2);
        Assert.Equal(["d1", "d2"], page1.DirectoryIds);
        Assert.NotNull(page1.NextPageToken);

        var page2 = await storage.ListDirectoryIdsAsync(null, pageSize: 2, pageToken: page1.NextPageToken);
        Assert.Equal(["d3"], page2.DirectoryIds);
        Assert.Null(page2.NextPageToken);
    }
}
