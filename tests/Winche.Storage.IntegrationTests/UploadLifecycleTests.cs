using Winche.Storage.Models;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

public class UploadLifecycleTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private FileStorage NewStorage(FakeArchive archive) =>
        new(fixture.DataSource, archive, new HookInvocationDispatcher([]));

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SetAsync_upserts_existing_path_and_bumps_version()
    {
        var storage = NewStorage(new FakeArchive());
        var first = await storage.SetAsync("docs/a.txt", "text/plain", 10);
        var second = await storage.SetAsync("docs/a.txt", "application/pdf", 99);

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal("application/pdf", second.MimeType);
        Assert.Equal(99, second.SizeBytes);
    }

    [Fact]
    public async Task ConfirmUploadAsync_single_put_marks_complete_and_persists_hash()
    {
        var archive = new FakeArchive { SingleObjectETag = "abc123" };
        var storage = NewStorage(archive);
        await storage.SetAsync("docs/a.txt", "text/plain", 10);

        var confirmed = await storage.ConfirmUploadAsync("docs/a.txt");

        Assert.Equal(UploadStatus.Complete, confirmed.UploadStatus);
        Assert.Equal("abc123", confirmed.ContentHash);
    }

    [Fact]
    public async Task ConfirmUploadAsync_throws_when_object_was_never_uploaded()
    {
        var archive = new FakeArchive { SingleObjectETag = null }; // no object in the archive
        var storage = NewStorage(archive);
        await storage.SetAsync("docs/a.txt", "text/plain", 10);

        await Assert.ThrowsAsync<FileNotUploadedException>(() => storage.ConfirmUploadAsync("docs/a.txt"));
    }

    [Fact]
    public async Task ConfirmUploadAsync_rejects_a_non_pending_file()
    {
        var storage = NewStorage(new FakeArchive());
        await storage.SetAsync("docs/a.txt", "text/plain", 10);
        await storage.ConfirmUploadAsync("docs/a.txt"); // now Complete

        await Assert.ThrowsAsync<InvalidUploadStatusException>(() => storage.ConfirmUploadAsync("docs/a.txt"));
    }

    [Fact]
    public async Task ConfirmUploadAsync_completes_multipart_and_clears_upload_id()
    {
        var archive = new FakeArchive { NextUploadId = "mp-7", MultipartETag = "mp-hash" };
        var storage = NewStorage(archive);
        await storage.SetAsync("docs/big.bin", "application/octet-stream", 1000);
        await storage.SignPartAsync("docs/big.bin", 1); // starts multipart, sets upload_id

        var confirmed = await storage.ConfirmUploadAsync("docs/big.bin");

        Assert.Equal(UploadStatus.Complete, confirmed.UploadStatus);
        Assert.Null(confirmed.UploadId);
        Assert.Equal("mp-hash", confirmed.ContentHash);
        Assert.Contains("mp-7", archive.CompletedUploadIds);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_reissue_aborts_the_existing_multipart()
    {
        var archive = new FakeArchive { NextUploadId = "mp-9" };
        var storage = NewStorage(archive);
        await storage.SetAsync("docs/big.bin", "application/octet-stream", 1000);
        await storage.SignPartAsync("docs/big.bin", 1); // upload_id = mp-9

        await storage.GenerateUploadUrlAsync("docs/big.bin");

        Assert.Contains("mp-9", archive.AbortedUploadIds);
        var reloaded = await storage.GetAsync("docs/big.bin");
        Assert.Null(reloaded!.UploadId);
    }

    [Fact]
    public async Task DeleteAsync_aborts_a_pending_multipart_upload()
    {
        var archive = new FakeArchive { NextUploadId = "mp-del" };
        var storage = NewStorage(archive);
        await storage.SetAsync("docs/big.bin", "application/octet-stream", 1000);
        await storage.SignPartAsync("docs/big.bin", 1); // pending + upload_id = mp-del

        var deleted = await storage.DeleteAsync("docs/big.bin");

        Assert.True(deleted);
        Assert.Contains("mp-del", archive.AbortedUploadIds);
        Assert.Null(await storage.GetAsync("docs/big.bin"));
    }
}
