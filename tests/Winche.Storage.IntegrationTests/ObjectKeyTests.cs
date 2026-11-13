using Winche.Storage.Models;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>Each upload has an archive key of its own: replacements, deletes and old links can't touch other bytes.</summary>
public class ObjectKeyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private readonly MemoryArchive _archive = new("mem");
    private FileStorage Storage() => new(fixture.DataSource, _archive, new HookInvocationDispatcher([]));

    private async Task<string> UploadAsync(FileStorage storage, string path, string text)
    {
        await storage.SetAsync(path, "text/plain", text.Length);
        var key = (await storage.GetAsync(path))!.UploadKey!;
        _archive.Put(key, text);
        await storage.ConfirmUploadAsync(path);
        return key;
    }

    [Fact]
    public async Task Keys_keep_the_folder_and_name_around_a_generation()
    {
        var storage = Storage();
        var key = (await storage.SetAsync("users/u1/photo.jpg", "image/jpeg", 1)).UploadKey!;
        Assert.Matches("^users/u1/[0-9a-f]{16}/photo.jpg$", key);
        Assert.Matches("^[0-9a-f]{16}/top.txt$", (await storage.SetAsync("top.txt", "text/plain", 1)).UploadKey!);
    }

    [Fact]
    public async Task A_replacement_keeps_the_old_bytes_until_its_own_are_confirmed()
    {
        var storage = Storage();
        var first = await UploadAsync(storage, "a.txt", "one");

        await storage.SetAsync("a.txt", "text/plain", 3);
        var second = (await storage.GetAsync("a.txt"))!.UploadKey!;
        Assert.NotEqual(first, second);
        Assert.True(_archive.Files.ContainsKey(first));   // still there while the new upload is pending

        _archive.Put(second, "two");
        var confirmed = await storage.ConfirmUploadAsync("a.txt");
        Assert.Equal(second, confirmed.ObjectKey);
        Assert.False(_archive.Files.ContainsKey(first));  // replaced bytes are gone
        Assert.EndsWith(second, (await storage.GenerateDownloadUrlAsync("a.txt")).Url);
    }

    [Fact]
    public async Task An_upload_to_an_old_link_doesnt_reach_the_new_registration()
    {
        var storage = Storage();
        await storage.SetAsync("a.txt", "text/plain", 3);
        var stale = (await storage.GetAsync("a.txt"))!.UploadKey!;
        await storage.SetAsync("a.txt", "text/plain", 3);   // registered again: a new key

        _archive.Put(stale, "old");                          // someone uses the first link
        await Assert.ThrowsAsync<FileNotUploadedException>(() => storage.ConfirmUploadAsync("a.txt"));
    }

    [Fact]
    public async Task A_file_recreated_after_a_delete_shares_nothing_with_the_old_one()
    {
        var storage = Storage();
        var old = await UploadAsync(storage, "a.txt", "old");
        await storage.DeleteAsync("a.txt");
        Assert.False(_archive.Files.ContainsKey(old));

        var again = await UploadAsync(storage, "a.txt", "new");
        Assert.NotEqual(old, again);
        Assert.Equal("new", System.Text.Encoding.UTF8.GetString(_archive.Files[again].Data));
    }

    [Fact]
    public async Task Server_code_writes_and_reads_bytes_without_links()
    {
        var storage = Storage();
        var text = System.Text.Encoding.UTF8.GetBytes("server report");
        var file = await storage.WriteAsync("reports/r1.txt", new MemoryStream(text), "text/plain", text.Length);
        Assert.Equal(UploadStatus.Complete, file.UploadStatus);

        await using (var read = await storage.OpenReadAsync("reports/r1.txt"))
        {
            using var copy = new MemoryStream();
            await read!.CopyToAsync(copy);
            Assert.Equal(text, copy.ToArray());
        }
        Assert.Null(await storage.OpenReadAsync("reports/missing.txt"));
        await Assert.ThrowsAsync<UploadSizeMismatchException>(() =>
            storage.WriteAsync("reports/r2.txt", new MemoryStream(text), "text/plain", text.Length + 1));   // held to its size
    }
}
