using Winche.Storage.Models;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

public class DeleteAndPurgeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private FileStorage NewStorage(FakeArchive archive) =>
        new(fixture.DataSource, archive, new HookInvocationDispatcher([]));

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DeleteAsync_removes_db_row_even_when_archive_delete_throws()
    {
        // The regression: before the DB-first reorder, an archive failure rolled the row back,
        // leaving an undeletable record. Now the commit lands and the archive error is swallowed.
        var archive = new FakeArchive { ThrowOnDeleteObjects = true };
        var storage = NewStorage(archive);
        await storage.SetAsync("docs/a.txt", "text/plain", 10);

        var result = await storage.DeleteAsync("docs/a.txt");

        Assert.True(result);
        Assert.Null(await storage.GetAsync("docs/a.txt"));
    }

    [Fact]
    public async Task DeleteAsync_returns_false_when_no_row_matches()
    {
        var archive = new FakeArchive();
        var storage = NewStorage(archive);

        var result = await storage.DeleteAsync("missing.txt");

        Assert.False(result);
        Assert.Empty(archive.DeletedKeys);
    }

    [Fact]
    public async Task DeleteAsync_removes_entire_subtree_and_forwards_keys_to_archive()
    {
        var archive = new FakeArchive();
        var storage = NewStorage(archive);
        await storage.SetAsync("a/x.txt", "text/plain", 1);
        await storage.SetAsync("a/b/y.txt", "text/plain", 1);
        await storage.SetAsync("other.txt", "text/plain", 1);

        var result = await storage.DeleteAsync("a");

        Assert.True(result);
        Assert.Null(await storage.GetAsync("a/x.txt"));
        Assert.Null(await storage.GetAsync("a/b/y.txt"));
        Assert.NotNull(await storage.GetAsync("other.txt"));
        Assert.Equal(["a/b/y.txt", "a/x.txt"], archive.DeletedKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PurgeOrphansAsync_deletes_only_old_unreferenced_objects()
    {
        var archive = new FakeArchive();
        var storage = NewStorage(archive);
        await storage.SetAsync("keep.txt", "text/plain", 1);

        var old = DateTime.UtcNow.AddHours(-2);
        var fresh = DateTime.UtcNow;
        archive.Objects.Add(new ArchivedObject("orphan-old.txt", old));     // no row + old   => purge
        archive.Objects.Add(new ArchivedObject("keep.txt", old));           // has row        => keep
        archive.Objects.Add(new ArchivedObject("orphan-fresh.txt", fresh)); // within grace   => keep

        var purged = await storage.PurgeOrphansAsync(prefix: null, graceWindow: TimeSpan.FromHours(1));

        Assert.Equal(1, purged);
        Assert.Equal(["orphan-old.txt"], archive.DeletedKeys);
    }
}
