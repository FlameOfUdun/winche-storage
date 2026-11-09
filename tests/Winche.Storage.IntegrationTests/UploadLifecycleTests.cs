using Winche.Storage.Models;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

public class UploadLifecycleTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private FileStorage NewStorage(FakeArchive archive) =>
        new(fixture.DataSource, archive, new HookInvocationDispatcher([]));

    private FileStorage LimitedStorage(FakeArchive archive, long max) =>
        new(fixture.DataSource, archive, new HookInvocationDispatcher([]), new StorageLimits(max));

    public Task InitializeAsync() => fixture.ResetAsync();

    [Fact]
    public async Task SetAsync_refuses_a_file_over_the_size_limit_and_a_negative_size()
    {
        var storage = LimitedStorage(new FakeArchive(), max: 100);

        await storage.SetAsync("docs/ok.bin", "application/octet-stream", 100);
        var tooBig = await Assert.ThrowsAsync<FileTooLargeException>(() => storage.SetAsync("docs/big.bin", "application/octet-stream", 101));
        Assert.Equal(100, tooBig.MaxBytes);
        await Assert.ThrowsAsync<FileTooLargeException>(() => storage.SetAsync("docs/neg.bin", "application/octet-stream", -1));
        Assert.Null(await storage.GetAsync("docs/big.bin"));
    }

    [Fact]
    public async Task ConfirmUploadAsync_refuses_multipart_parts_that_exceed_the_declared_size()
    {
        // Parts are signed without a length: a client registers 1000 bytes, then sends more.
        var archive = new FakeArchive { NextUploadId = "mp-x", PartSizes = [5_000_000, 5_000_000] };
        var storage = NewStorage(archive);
        await storage.SetAsync("docs/big.bin", "application/octet-stream", 1000);
        await storage.SignPartAsync("docs/big.bin", 1);

        var ex = await Assert.ThrowsAsync<UploadSizeMismatchException>(() => storage.ConfirmUploadAsync("docs/big.bin"));

        Assert.Equal(10_000_000, ex.Actual);
        Assert.Contains("mp-x", archive.AbortedUploadIds);
        Assert.DoesNotContain("mp-x", archive.CompletedUploadIds);
        var file = await storage.GetAsync("docs/big.bin");
        Assert.Equal(UploadStatus.Pending, file!.UploadStatus);
        Assert.Null(file.UploadId);
    }
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
        var archive = new FakeArchive { NextUploadId = "mp-7", MultipartETag = "mp-hash", PartSizes = [600, 400] };
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
