using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>file: for the database's expand: a file path to its record, as the caller sees it.</summary>
public class FileReferenceResolverTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_file_path_resolves_to_its_record_when_the_caller_may_get_it()
    {
        var cs = fixture.DataSource.ConnectionString;
        if (!cs.Contains("Password", StringComparison.OrdinalIgnoreCase)) cs = new NpgsqlConnectionStringBuilder(cs) { Password = "postgres" }.ConnectionString;
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWincheStorage(o =>
        {
            o.ConnectionString = cs;
            o.UseRulesText("""
                match /scans/{uid}/{rest=**} { allow read: if request.auth.uid == uid; }
                """);
        });
        using var host = builder.Build();
        await host.InitializeWincheStorageAsync();
        var storage = host.Services.GetRequiredService<FileStorage>();
        await storage.SetAsync("scans/ann/a.png", "image/png", 10, new JsonObject { ["ward"] = "icu" });
        await storage.SetAsync("scans/bob/b.png", "image/png", 20);

        var resolver = host.Services.GetServices<IWincheReferenceResolver>().Single(r => r.Kind == "file");
        Assert.Equal("winche_files_feed_events", resolver.ChangeChannel);
        var ann = new ReferenceCaller(new Dictionary<string, object?> { ["uid"] = "ann" }, false);
        var found = await resolver.ResolveAsync(["scans/ann/a.png", "scans/bob/b.png", "scans/ann/missing.png", "not a path//"], ann, default);
        Assert.Equal(["scans/ann/a.png"], found.Keys);
        var record = found["scans/ann/a.png"];
        Assert.Equal(("image/png", 10L, "icu"), ((string?)record["mimeType"], (long?)record["sizeBytes"], (string?)record["metadata"]!["ward"]));
        Assert.False(record.ContainsKey("objectKey"));   // nothing internal

        var server = await resolver.ResolveAsync(["scans/bob/b.png"], ReferenceCaller.Server, default);
        Assert.Single(server);   // the app's own code: no rules
    }
}
