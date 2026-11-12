using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Config;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;
using Winche.Storage.Locations;
using Winche.Storage.Models;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>An archive in memory that can be read from and written to (so files can be moved in and out).</summary>
internal sealed class MemoryArchive(string name) : IArchive, IArchiveContent
{
    public ConcurrentDictionary<string, (byte[] Data, string? Type)> Files { get; } = new();
    public string Name => name;

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(Files.TryGetValue(path, out var f) ? new MemoryStream(f.Data) : null);

    public async Task WriteAsync(string path, Stream content, string? contentType, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        Files[path] = (buffer.ToArray(), contentType);
    }

    public void Put(string path, string text) => Files[path] = (Encoding.UTF8.GetBytes(text), "text/plain");

    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) =>
        Task.FromResult(new UploadSession { Url = $"mem://{name}/{path}" });
    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new DownloadSession { Url = $"mem://{name}/{path}", ExpiresAt = default });
    public Task<string?> GetObjectETagAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(Files.TryGetValue(path, out var f) ? $"\"{f.Data.Length}\"" : null);
    public Task DeleteAsync(string path, CancellationToken ct = default) { Files.TryRemove(path, out _); return Task.CompletedTask; }
    public Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default) { foreach (var p in paths) Files.TryRemove(p, out _); return Task.CompletedTask; }
    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var k in Files.Keys.Where(k => prefix is null || k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            yield return new ArchivedObject(k, DateTime.UtcNow.AddDays(-1));
    }
    public Task<string> CreateMultipartUploadAsync(string path, string mimeType, CancellationToken ct = default) => Task.FromResult($"{name}-upload");
    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default) =>
        Task.FromResult(new UploadSession { Url = $"mem://{name}/{path}?part={partNumber}" });
    public Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => Task.FromResult<string?>("etag");
    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default) =>
        uploadId.StartsWith(name, StringComparison.Ordinal) ? Task.FromResult<IEnumerable<FilePart>>([]) : throw new InvalidOperationException("NoSuchUpload");
    public Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>"Buckets" in memory, standing in for S3.</summary>
internal sealed class MemoryBuckets : IArchiveProvider
{
    public ConcurrentDictionary<string, MemoryArchive> Buckets { get; } = new();
    public string Kind => "s3";
    public IArchive Create(ArchiveLocation location, string? secretKey)
    {
        if (secretKey != "right") throw new InvalidOperationException("The access key or secret is wrong.");
        return Buckets.GetOrAdd(location.S3!.Bucket, b => new MemoryArchive(b));
    }
}

/// <summary>Moving files to another location as guided steps: copy, switch, finish.</summary>
public class StorageMoveTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var cmd = fixture.DataSource.CreateCommand("DROP TABLE IF EXISTS winche_settings; DROP TABLE IF EXISTS winche_secrets; DROP TABLE IF EXISTS winche_storage_moves;");
        await cmd.ExecuteNonQueryAsync();
        StorageMoveWorker.Tick = TimeSpan.FromMilliseconds(100);
        StorageMoveWorker.CatchUpEvery = TimeSpan.FromMilliseconds(200);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private string ConnectionString
    {
        get
        {
            var cs = fixture.DataSource.ConnectionString;
            return cs.Contains("Password", StringComparison.OrdinalIgnoreCase) ? cs : new NpgsqlConnectionStringBuilder(cs) { Password = "postgres" }.ConnectionString;
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition(), $"Timed out waiting for {what}.");
    }

    private static readonly ArchiveLocation Bucket = new()
    {
        Id = "r2-main", Kind = "s3", S3 = new S3LocationSettings { Bucket = "smartairway-files", Region = "auto", ServiceUrl = "https://acct.r2.cloudflarestorage.com", AccessKey = "AK" },
    };

    [Fact]
    public async Task Files_are_copied_then_switched_with_reads_falling_back_until_finished()
    {
        var old = new MemoryArchive("old");
        var buckets = new MemoryBuckets();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWincheConfig(o => { o.ConnectionString = ConnectionString; o.MasterKey = "storage-move-tests-master-key-0123456789"; });
        builder.Services.AddWincheStorage(o => { o.ConnectionString = ConnectionString; o.UseSettings(); });
        builder.Services.AddSingleton<IArchive>(old);   // the app's own location, registered after, as hosts do
        builder.Services.AddSingleton<IArchiveProvider>(buckets);
        using var host = builder.Build();
        await host.InitializeWincheStorageAsync();
        await host.StartAsync();
        try
        {
            var storage = host.Services.GetRequiredService<FileStorage>();
            var moves = host.Services.GetRequiredService<StorageMoves>();
            await storage.SetAsync("docs/a.txt", "text/markdown", 5);
            old.Put("docs/a.txt", "hello");
            await storage.ConfirmUploadAsync("docs/a.txt");
            old.Put("docs/c.txt", "gone soon");
            Assert.Equal("mem://old/docs/a.txt", (await storage.GenerateDownloadUrlAsync("docs/a.txt")).Url);

            // 1. Test the new place: a wrong secret fails at Connect; the right one passes every step.
            Assert.False((await moves.TestAsync(Bucket, "wrong")).Ok);
            var test = await moves.TestAsync(Bucket, "right");
            Assert.True(test.Ok, string.Join("; ", test.Steps.Select(s => $"{s.Name}: {s.Error}")));
            Assert.Empty(buckets.Buckets["smartairway-files"].Files);   // the test file is gone

            // 2. Copy: the old location stays in use; when everything's there the move is ready.
            await moves.StartAsync(Bucket, "right", "admin@x", "Move to R2");
            await Assert.ThrowsAsync<StorageMoveException>(() => moves.StartAsync(Bucket, "right", "admin@x", "again"));
            await Until(() => moves.State.Move?.Phase == StorageMove.Ready, "the copy");
            var bucket = buckets.Buckets["smartairway-files"];
            Assert.Equal(("hello", "text/markdown"), (Encoding.UTF8.GetString(bucket.Files["docs/a.txt"].Data), bucket.Files["docs/a.txt"].Type));
            Assert.Equal("mem://old/docs/a.txt", (await storage.GenerateDownloadUrlAsync("docs/a.txt")).Url);   // still the old one
            var progress = (await moves.ProgressAsync())!;
            Assert.Equal((2, 2, 0), (progress.Total, progress.Copied, progress.Failed));

            // A delete during the move reaches both places; a file arriving after the copy waits for the catch-up.
            await host.Services.GetRequiredService<Locations.ArchiveRouter>().DeleteAsync("docs/c.txt");
            Assert.False(old.Files.ContainsKey("docs/c.txt") || bucket.Files.ContainsKey("docs/c.txt"));
            await storage.SetAsync("docs/b.txt", "text/plain", 5);
            old.Put("docs/b.txt", "later");
            await storage.ConfirmUploadAsync("docs/b.txt");

            // 3. Switch: new uploads go to the bucket; b (only in the old place for now) is still readable.
            await moves.SwitchAsync("admin@x", "Everything is copied");
            Assert.Equal(StorageMove.Switched, moves.State.Move?.Phase);
            Assert.Equal("r2-main", moves.State.Active?.Id);
            await storage.SetAsync("docs/new.txt", "text/plain", 3);
            Assert.StartsWith("mem://smartairway-files/", (await storage.GenerateUploadUrlAsync("docs/new.txt")).Url);
            await Until(() => bucket.Files.ContainsKey("docs/b.txt"), "the catch-up copy");
            Assert.Equal("mem://smartairway-files/docs/b.txt", (await storage.GenerateDownloadUrlAsync("docs/b.txt")).Url);
            await Assert.ThrowsAsync<StorageMoveException>(() => moves.CancelAsync("admin@x", "Change my mind"));

            // 4. Finish: the old place is left alone from now on (its files stay, to delete by hand).
            await Until(() => moves.ProgressAsync().Result is { PassFinishedAt: not null, Failed: 0 } p && p.PassFinishedAt > DateTimeOffset.UtcNow.AddSeconds(-1), "a clean catch-up");
            await moves.FinishAsync("admin@x", "Done");
            Assert.Null(moves.State.Move);
            Assert.True(old.Files.ContainsKey("docs/a.txt"));
            var history = await host.Services.GetRequiredService<SettingsStore>().HistoryAsync(StorageLocationSettings.Area);
            Assert.Equal(["Done", "Everything is copied", "Every file is copied: ready to switch", "Move to R2"], history.Select(h => h.Reason));
        }
        finally { await host.StopAsync(); }
    }
}
