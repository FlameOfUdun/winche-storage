using System.Text;
using Amazon.S3;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.LocalStack;
using Winche.Core;
using Winche.Storage.DependencyInjection;
using Winche.Storage.FileSystem;
using Winche.Storage.Interfaces;
using Winche.Storage.Locations;
using Winche.Storage.S3.DependencyInjection;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>A real move: files in a local folder (the app's own location) to an S3 bucket (LocalStack).</summary>
public class S3MoveTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly LocalStackContainer _s3 = new LocalStackBuilder().WithImage("localstack/localstack:4").Build();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"winche-s3-move-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var cmd = fixture.DataSource.CreateCommand("DROP TABLE IF EXISTS winche_schema; DROP TABLE IF EXISTS winche_settings; DROP TABLE IF EXISTS winche_secrets; DROP TABLE IF EXISTS winche_storage_moves;");
        await cmd.ExecuteNonQueryAsync();
        await _s3.StartAsync();
        using var client = new AmazonS3Client("test", "test", new AmazonS3Config { ServiceURL = _s3.GetConnectionString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        await client.PutBucketAsync("app-files");
        StorageMoveWorker.Tick = TimeSpan.FromMilliseconds(100);
        StorageMoveWorker.CatchUpEvery = TimeSpan.FromMilliseconds(200);
    }

    public async Task DisposeAsync()
    {
        await _s3.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string ConnectionString
    {
        get
        {
            var cs = fixture.DataSource.ConnectionString;
            return cs.Contains("Password", StringComparison.OrdinalIgnoreCase) ? cs : new NpgsqlConnectionStringBuilder(cs) { Password = "postgres" }.ConnectionString;
        }
    }

    [Fact]
    public async Task Files_move_from_a_folder_to_an_s3_bucket()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWincheConfig(o => { o.ConnectionString = ConnectionString; o.MasterKey = "s3-move-tests-master-key-0123456789abcdef"; });
        builder.Services.AddWincheStorage(o =>
        {
            o.ConnectionString = ConnectionString;
            o.UseFileSystemArchive(fs => { fs.Root = _root; fs.SigningKey = "a-test-signing-key-that-is-long-enough"; fs.PublicBaseUrl = "http://localhost"; });
            o.AllowS3Locations();
            o.UseSettings();
        });
        using var host = builder.Build();
        await host.InitializeWincheStorageAsync();
        await host.StartAsync();
        try
        {
            var folder = host.Services.GetRequiredService<FileSystemArchive>();
            var storage = host.Services.GetRequiredService<FileStorage>();
            var moves = host.Services.GetRequiredService<StorageMoves>();
            var big = new byte[3 * 1024 * 1024];
            Random.Shared.NextBytes(big);
            await storage.SetAsync("reports/2026/q3.pdf", "application/pdf", big.Length);
            var key = (await storage.GetAsync("reports/2026/q3.pdf"))!.UploadKey!;
            await folder.WriteAsync(key, new MemoryStream(big), "application/pdf");
            await storage.ConfirmUploadAsync("reports/2026/q3.pdf");

            var bucket = new ArchiveLocation
            {
                Id = "aws-main", Kind = "s3",
                S3 = new S3LocationSettings { Bucket = "app-files", Region = "us-east-1", ServiceUrl = _s3.GetConnectionString(), AccessKey = "test", ForcePathStyle = true },
            };
            var test = await moves.TestAsync(bucket, "test");
            Assert.True(test.Ok, string.Join("; ", test.Steps.Select(s => $"{s.Name}: {s.Error}")));
            Assert.False((await moves.TestAsync(bucket with { S3 = bucket.S3! with { Bucket = "no-such-bucket" } }, "test")).Ok);

            await moves.StartAsync(bucket, "test", "admin@x", "To S3");
            for (var i = 0; i < 100 && moves.State.Move?.Phase != StorageMove.Ready; i++) await Task.Delay(100);
            Assert.Equal(StorageMove.Ready, moves.State.Move?.Phase);
            await moves.SwitchAsync("admin@x", "Copied");

            var s3 = (IArchiveContent)await host.Services.GetRequiredService<ArchiveLocations>().GetAsync(bucket);
            await using (var copy = await s3.OpenReadAsync(key))
            {
                using var bytes = new MemoryStream();
                await copy!.CopyToAsync(bytes);
                Assert.Equal(big, bytes.ToArray());
            }
            var link = (await storage.GenerateDownloadUrlAsync("reports/2026/q3.pdf")).Url;
            Assert.StartsWith(_s3.GetConnectionString().TrimEnd('/') + "/app-files/", link);   // http, like the service
            using (var http = new HttpClient()) Assert.Equal(big, await http.GetByteArrayAsync(link));
            Assert.Null(await s3.OpenReadAsync("reports/missing.pdf"));
        }
        finally { await host.StopAsync(); }
    }
}
