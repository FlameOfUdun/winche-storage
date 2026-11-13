using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Storage.Abstraction;
using Winche.Storage.Authorization;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>Before hooks on files: in the write's transaction, after the rules; they refuse or change metadata.</summary>
public class BeforeHookTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Caller : IRuleClaimsAccessor
    {
        public IReadOnlyDictionary<string, object?>? Claims;
        public IReadOnlyDictionary<string, object?>? GetClaims() => Claims;
    }

    private sealed class Guard : FileBeforeHook
    {
        public List<(string Path, FileOperation Op)> Seen { get; } = [];
        public override Task OnBeforeWriteAsync(FileWrite write, CancellationToken ct)
        {
            lock (Seen) Seen.Add((write.Path, write.Operation));
            if (write.Before?.MetaData["locked"]?.GetValue<bool>() == true)
                write.Deny(write.Operation == FileOperation.Delete ? "Locked files can't be deleted." : "This file is locked.");
            if (write.Operation != FileOperation.Delete)
            {
                write.Metadata!["owner"] = write.Uid ?? "server";
                write.Metadata.Remove("secret");
                write.CacheControl = "private, max-age=60";
            }
            return Task.CompletedTask;
        }
    }

    private string ConnectionString
    {
        get
        {
            var cs = fixture.DataSource.ConnectionString;
            return cs.Contains("Password", StringComparison.OrdinalIgnoreCase) ? cs : new NpgsqlConnectionStringBuilder(cs) { Password = "postgres" }.ConnectionString;
        }
    }

    private async Task<(IHost Host, Guard Guard, Caller Caller)> StartAsync()
    {
        var guard = new Guard();
        var caller = new Caller();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWincheStorage(o =>
        {
            o.ConnectionString = ConnectionString;
            o.UseRulesText("match /scans/{rest=**} { allow read, create, update: if request.auth != null; }");
            o.UseHooks(h => h.Before("scans/{rest=**}", guard));
        });
        builder.Services.AddSingleton<IRuleClaimsAccessor>(caller);
        var host = builder.Build();
        await host.InitializeWincheStorageAsync();
        return (host, guard, caller);
    }

    [Fact]
    public async Task A_hook_changes_the_metadata_and_headers_saved_for_callers_and_the_server()
    {
        var (host, guard, caller) = await StartAsync();
        using var _ = host;
        caller.Claims = new Dictionary<string, object?> { ["uid"] = "ann" };
        var guarded = host.Services.GetRequiredService<IFileStorage>();
        var file = await guarded.SetAsync("scans/a.png", "image/png", 10, new JsonObject { ["secret"] = "x", ["ward"] = "icu" });
        Assert.Equal(("ann", "icu", false), ((string?)file.MetaData["owner"], (string?)file.MetaData["ward"], file.MetaData.ContainsKey("secret")));
        Assert.Equal("private, max-age=60", file.CacheControl);

        var updated = await guarded.UpdateMetadataAsync("scans/a.png", new JsonObject { ["secret"] = "y", ["note"] = "n" });
        Assert.Equal(("n", false), ((string?)updated!.MetaData["note"], updated.MetaData.ContainsKey("secret")));

        caller.Claims = null;   // the app's own write
        var server = await host.Services.GetRequiredService<FileStorage>().SetAsync("scans/b.png", "image/png", 10);
        Assert.Equal("server", (string?)server.MetaData["owner"]);
        Assert.Equal([("scans/a.png", FileOperation.Create), ("scans/a.png", FileOperation.Update), ("scans/b.png", FileOperation.Create)], guard.Seen);
    }

    [Fact]
    public async Task A_hook_refuses_and_a_directory_delete_with_one_refusal_deletes_nothing()
    {
        var (host, guard, caller) = await StartAsync();
        using var _ = host;
        var storage = host.Services.GetRequiredService<FileStorage>();
        await storage.SetAsync("scans/w1", "application/json", 2);
        await storage.SetAsync("scans/w1/a.png", "image/png", 10);
        await storage.SetAsync("scans/w1/b.png", "image/png", 10, new JsonObject { ["locked"] = true });

        caller.Claims = new Dictionary<string, object?> { ["uid"] = "ann" };
        var refused = await Assert.ThrowsAsync<WincheDeniedException>(() =>
            host.Services.GetRequiredService<IFileStorage>().UpdateMetadataAsync("scans/w1/b.png", new JsonObject { ["x"] = 1 }));
        Assert.Equal(("This file is locked.", WincheErrorCode.PermissionDenied), (refused.Message, refused.Error.Kind));

        var deleting = await Assert.ThrowsAsync<WincheDeniedException>(() => storage.DeleteAsync("scans/w1"));
        Assert.Equal("Locked files can't be deleted.", deleting.Message);
        Assert.NotNull(await storage.GetAsync("scans/w1/a.png"));
        Assert.NotNull(await storage.GetAsync("scans/w1"));
        Assert.Contains(("scans/w1/a.png", FileOperation.Delete), guard.Seen);   // each file under it was asked
    }

    [Fact]
    public async Task Rules_refuse_before_any_hook_runs()
    {
        var (host, guard, caller) = await StartAsync();
        using var _ = host;
        caller.Claims = null;   // signed out: the rules refuse
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Services.GetRequiredService<IFileStorage>().SetAsync("scans/c.png", "image/png", 10));
        Assert.Empty(guard.Seen);
    }
}
