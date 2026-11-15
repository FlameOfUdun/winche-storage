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
    public async Task UpdateMetadataAsync_merges_and_a_null_removes_a_key()
    {
        var storage = NewStorage();
        await storage.SetAsync("docs/a.txt", "text/plain", 1, new JsonObject { ["a"] = 1, ["c"] = 3 });

        var updated = await storage.UpdateMetadataAsync("docs/a.txt", new JsonObject { ["b"] = 2, ["c"] = null });

        Assert.NotNull(updated);
        Assert.Equal(1, (int)updated!.MetaData["a"]!);   // kept
        Assert.Equal(2, (int)updated.MetaData["b"]!);    // added
        Assert.False(updated.MetaData.ContainsKey("c"));  // removed
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
        var jsonInA = await storage.ListAsync("a", new(MimeType: "application/json"));

        Assert.Equal(2, inA.Items.Count);
        Assert.Equal(["a/y.json"], jsonInA.Items.Select(r => r.Path));
    }

    [Fact]
    public async Task ListAsync_pages_through_files_and_folders_in_name_order()
    {
        var storage = NewStorage();
        foreach (var p in new[] { "docs/b.txt", "docs/d.txt", "docs/a/1.txt", "docs/c/2.txt", "docs/c/deeper/3.txt", "top.txt" })
            await storage.SetAsync(p, "text/plain", 1);

        var all = await storage.ListAsync("docs");
        Assert.Equal(["docs/b.txt", "docs/d.txt"], all.Items.Select(f => f.Path));
        Assert.Equal(["docs/a", "docs/c"], all.Prefixes);
        Assert.Null(all.NextPageToken);

        // Pages of two, merged by name: a/, b.txt | c/, d.txt.
        var first = await storage.ListAsync("docs", new(MaxResults: 2));
        Assert.Equal(["docs/b.txt"], first.Items.Select(f => f.Path));
        Assert.Equal(["docs/a"], first.Prefixes);
        var second = await storage.ListAsync("docs", new(MaxResults: 2, PageToken: first.NextPageToken));
        Assert.Equal(["docs/d.txt"], second.Items.Select(f => f.Path));
        Assert.Equal(["docs/c"], second.Prefixes);
        Assert.Null(second.NextPageToken);

        var top = await storage.ListAsync("");
        Assert.Equal(["top.txt"], top.Items.Select(f => f.Path));
        Assert.Equal(["docs"], top.Prefixes);
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
