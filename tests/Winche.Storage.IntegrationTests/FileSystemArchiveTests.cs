using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Winche.Storage.FileSystem;
using Winche.Storage.Models;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

public sealed class FileSystemArchiveTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("winche-fs-").FullName;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private FileSystemArchive _archive = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _archive = new FileSystemArchive(Options.Create(new FileSystemArchiveOptions
        {
            Root = _root, SigningKey = "a-test-signing-key-that-is-long-enough", PublicBaseUrl = "http://localhost", MaxPartBytes = 1024,
        }), new HttpContextAccessor(), _clock);
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Logging.ClearProviders();
        b.Services.AddSingleton(_archive);
        _app = b.Build();
        _app.MapWincheStorageFileSystem();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private static string Rel(string url) => new Uri(url).PathAndQuery;

    private Task<HttpResponseMessage> Put(string url, byte[] body) => _http.PutAsync(Rel(url), new ByteArrayContent(body));

    [Fact]
    public async Task Files_go_up_and_come_down_through_signed_urls()
    {
        var bytes = Encoding.UTF8.GetBytes("hello, files");
        var up = await _archive.GenerateUploadUrlAsync("users/u1/notes v1.txt", "text/plain", bytes.Length);
        Assert.StartsWith("http://localhost/_files/o/users/u1/notes%20v1.txt?exp=", up.Url);
        var put = await Put(up.Url, bytes);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal($"\"{Convert.ToHexStringLower(MD5.HashData(bytes))}\"", put.Headers.ETag?.Tag);
        Assert.Equal(put.Headers.ETag?.Tag, await _archive.GetObjectETagAsync("users/u1/notes v1.txt"));
        Assert.True(File.Exists(Path.Combine(_root, "objects", "users", "u1", "notes v1.txt")));

        var down = await _archive.GenerateDownloadUrlAsync("users/u1/notes v1.txt");
        var get = await _http.GetAsync(Rel(down.Url));
        Assert.Equal("hello, files", await get.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", get.Content.Headers.ContentType?.MediaType);

        using var range = new HttpRequestMessage(HttpMethod.Get, Rel(down.Url)) { Headers = { Range = new(7, 11) } };
        var partial = await _http.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("files", await partial.Content.ReadAsStringAsync());

        var keys = new List<string>();
        await foreach (var o in _archive.ListObjectsAsync("users/")) keys.Add(o.Key);
        Assert.Equal(["users/u1/notes v1.txt"], keys);
        await _archive.DeleteAsync("users/u1/notes v1.txt");
        Assert.Null(await _archive.GetObjectETagAsync("users/u1/notes v1.txt"));
        Assert.False(Directory.Exists(Path.Combine(_root, "objects", "users")));   // empty folders go too
    }

    [Fact]
    public async Task Urls_only_do_what_they_were_signed_for()
    {
        var up = await _archive.GenerateUploadUrlAsync("a/b.bin", "application/octet-stream", 4);
        var down = await _archive.GenerateDownloadUrlAsync("a/b.bin");

        // Another path, a changed size limit, a GET signature used to PUT, a forged signature.
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(up.Url.Replace("/a/b.bin", "/a/c.bin"), [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(up.Url.Replace("max=4", "max=999"), [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(down.Url, [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync(Rel(up.Url))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync(Rel(down.Url[..^4] + "0000"))).StatusCode);

        // More bytes than were declared: refused, nothing kept.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Put(up.Url, [1, 2, 3, 4, 5])).StatusCode);
        Assert.Null(await _archive.GetObjectETagAsync("a/b.bin"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "objects"), "*", SearchOption.AllDirectories));

        // An expired URL.
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(up.Url, [1])).StatusCode);
    }

    [Fact]
    public async Task Paths_cant_leave_the_folder()
    {
        foreach (var bad in new[] { "../outside.txt", "a/../../outside.txt", "a//b", "/", "a/./b", "a/.winche-x.tmp", "C:/x" })
            await Assert.ThrowsAsync<ArgumentException>(() => _archive.GenerateUploadUrlAsync(bad, "text/plain", 1));
        // A hand-built URL with an escaped "..": refused by its signature, and by the path check behind it.
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.PutAsync("/_files/o/..%2F..%2Fx?exp=9999999999&max=1&sig=00", new ByteArrayContent([1]))).StatusCode);
    }

    [Fact]
    public async Task Multipart_uploads_assemble_in_order()
    {
        var id = await _archive.CreateMultipartUploadAsync("big/file.bin", "application/octet-stream");
        var parts = new[] { new byte[1000], new byte[1000], new byte[10] };
        for (var i = 0; i < parts.Length; i++) Array.Fill(parts[i], (byte)(i + 1));
        // Out of order on purpose.
        foreach (var n in new[] { 3, 1, 2 })
        {
            var signed = await _archive.SignPartAsync("big/file.bin", id, n);
            Assert.Equal(HttpStatusCode.OK, (await Put(signed.Url, parts[n - 1])).StatusCode);
        }
        var tooBig = await _archive.SignPartAsync("big/file.bin", id, 4);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Put(tooBig.Url, new byte[2000])).StatusCode);   // MaxPartBytes 1024

        Assert.Equal([1000L, 1000L, 10L], (await _archive.ListPartsAsync("big/file.bin", id)).Select(p => p.Size));
        Assert.NotNull(await _archive.CompleteMultipartUploadAsync("big/file.bin", id));
        var all = await File.ReadAllBytesAsync(Path.Combine(_root, "objects", "big", "file.bin"));
        Assert.Equal(parts.SelectMany(p => p).ToArray(), all);
        Assert.False(Directory.Exists(Path.Combine(_root, "parts", id)));
        Assert.Null(await _archive.CompleteMultipartUploadAsync("big/file.bin", id));   // already done
    }

    [Fact]
    public async Task Works_as_Winche_Storages_archive()
    {
        var storage = new FileStorage(fixture.DataSource, _archive, new HookInvocationDispatcher([]));
        var bytes = Encoding.UTF8.GetBytes("report body");
        await storage.SetAsync("reports/r1.pdf", "application/pdf", bytes.Length);
        var upload = await storage.GenerateUploadUrlAsync("reports/r1.pdf");
        Assert.Equal(HttpStatusCode.OK, (await Put(upload.Url, bytes)).StatusCode);
        var confirmed = await storage.ConfirmUploadAsync("reports/r1.pdf");
        Assert.Equal(UploadStatus.Complete, confirmed.UploadStatus);
        var download = await storage.GenerateDownloadUrlAsync("reports/r1.pdf");
        Assert.Equal(bytes, await _http.GetByteArrayAsync(Rel(download.Url)));
    }

    [Fact]
    public void Without_a_key_of_its_own_it_signs_with_the_master_key()
    {
        var keys = new Winche.Core.WincheKeys(new string('m', 40));
        var options = new FileSystemArchiveOptions { Root = Path.Combine(Path.GetTempPath(), "winche-fs-" + Guid.NewGuid().ToString("N")) };
        _ = new FileSystemArchive(Options.Create(options), new HttpContextAccessor(), TimeProvider.System, keys);
        Assert.Equal(keys.Derive("file-links"), options.SigningKey);
        Assert.Throws<InvalidOperationException>(() => new FileSystemArchive(
            Options.Create(new FileSystemArchiveOptions { Root = options.Root }), new HttpContextAccessor(), TimeProvider.System));
        Directory.Delete(options.Root, recursive: true);
    }
}
