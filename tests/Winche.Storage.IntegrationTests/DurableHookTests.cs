using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Abstraction;
using Winche.Storage.Models;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>8.3.0: file hooks are delivered from Postgres — durable, retried, dead-lettered, once per node set.</summary>
public class DurableHookTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Recorder : FileStoreHook
    {
        public readonly ConcurrentQueue<string> Events = new();
        public Func<string, bool>? FailWhen;

        private Task Record(string e)
        {
            if (FailWhen?.Invoke(e) == true)
                throw new InvalidOperationException("boom: " + e);
            Events.Enqueue(e);
            return Task.CompletedTask;
        }

        public override Task OnFileRegisteredAsync(FileRecord r, CancellationToken ct) => Record($"registered {r.Path} {r.MimeType}");
        public override Task OnUploadConfirmedAsync(FileRecord r, CancellationToken ct) => Record($"confirmed {r.Path} {r.ContentHash}");
        public override Task OnMetadataUpdatedAsync(FileRecord r, CancellationToken ct) => Record($"metadata {r.Path} {r.MetaData["k"]}");
        public override Task OnFileDeletedAsync(string path, CancellationToken ct) => Record($"deleted {path}");
    }

    private static readonly HookDeliveryOptions Fast = new() { MaxBackoff = TimeSpan.Zero };

    private (FileStorage Storage, HookDeliveryService Worker) Node(FileStoreHook hook, string pattern = "docs/{file=**}", HookDeliveryOptions? options = null)
    {
        var dispatcher = new HookInvocationDispatcher([new HookRegistration(pattern, hook)]);
        return (new FileStorage(fixture.DataSource, new FakeArchive(), dispatcher),
                new HookDeliveryService(fixture.DataSource, dispatcher, options ?? Fast));
    }

    private static async Task DrainAsync(params HookDeliveryService[] workers)
    {
        for (var i = 0; i < 50; i++)
        {
            var ran = 0;
            foreach (var w in workers) ran += await w.RunOnceAsync(default);
            if (ran == 0) return;
        }
    }

    private async Task<long> PendingAsync()
    {
        await using var cmd = fixture.DataSource.CreateCommand("SELECT count(*) FROM winche_storage_hook_deliveries");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task MakeDueAsync()
    {
        await using var cmd = fixture.DataSource.CreateCommand("UPDATE winche_storage_hook_deliveries SET next_attempt_at = now()");
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Every_file_change_reaches_the_hook_in_order()
    {
        var hook = new Recorder();
        var (storage, worker) = Node(hook);
        await storage.SetAsync("docs/a.pdf", "application/pdf", 0);
        await storage.ConfirmUploadAsync("docs/a.pdf");
        await storage.UpdateMetadataAsync("docs/a.pdf", new() { ["k"] = "v" });
        await storage.SetAsync("other/b.pdf", "application/pdf", 0);   // pattern doesn't match
        await storage.DeleteAsync("docs/a.pdf");

        await DrainAsync(worker);

        Assert.Equal(["registered docs/a.pdf application/pdf", "confirmed docs/a.pdf etag-single", "metadata docs/a.pdf v", "deleted docs/a.pdf"],
            hook.Events);
        Assert.Equal(0, await PendingAsync());   // delivered rows are removed
    }

    [Fact]
    public async Task A_change_made_while_no_worker_runs_is_delivered_after_a_restart()
    {
        var before = new Recorder();
        var (storage, _) = Node(before);
        await storage.SetAsync("docs/a.pdf", "application/pdf", 0);   // the process "dies" here

        var after = new Recorder();
        var (_, restarted) = Node(after);   // same hook type and pattern: the same hook
        await DrainAsync(restarted);
        Assert.Equal(["registered docs/a.pdf application/pdf"], after.Events);
    }

    [Fact]
    public async Task A_failing_hook_is_retried_and_holds_back_that_files_later_events()
    {
        var aFailures = 2;
        var hook = new Recorder { FailWhen = e => e.Contains("a.pdf") && Interlocked.Decrement(ref aFailures) >= 0 };
        var (storage, worker) = Node(hook);
        await storage.SetAsync("docs/a.pdf", "application/pdf", 0);
        await storage.ConfirmUploadAsync("docs/a.pdf");
        await storage.SetAsync("docs/b.pdf", "text/plain", 0);

        await worker.RunOnceAsync(default);   // a's registered fails (b's goes through)
        await MakeDueAsync();
        await worker.RunOnceAsync(default);   // a's registered fails again; a's confirmed still waits
        Assert.Equal(["registered docs/b.pdf text/plain"], hook.Events);

        await MakeDueAsync();
        await DrainAsync(worker);
        Assert.Equal(["registered docs/b.pdf text/plain", "registered docs/a.pdf application/pdf", "confirmed docs/a.pdf etag-single"], hook.Events);
    }

    [Fact]
    public async Task A_delivery_that_keeps_failing_becomes_a_dead_letter_and_can_be_retried()
    {
        var hook = new Recorder { FailWhen = e => e.StartsWith("registered") };
        var (storage, worker) = Node(hook, options: Fast with { MaxRetryAge = TimeSpan.Zero });
        await storage.SetAsync("docs/a.pdf", "application/pdf", 0);
        await storage.ConfirmUploadAsync("docs/a.pdf");

        await DrainAsync(worker);
        var dead = Assert.Single(await new StorageHookDeadLetters(fixture.DataSource).ListAsync());
        Assert.Equal(("registered", "docs/a.pdf", 1), (dead.Kind, dead.Path, dead.Attempts));
        Assert.Contains("boom", dead.LastError);
        Assert.Equal(["confirmed docs/a.pdf etag-single"], hook.Events);   // a dead letter doesn't block later events
        var status = await new StorageHookDeadLetters(fixture.DataSource).StatusAsync();
        Assert.Equal((0L, 1L), (status.Pending, status.DeadLetters));   // what the health check reports

        hook.FailWhen = null;
        Assert.Equal(1, await new StorageHookDeadLetters(fixture.DataSource).RetryAsync());
        await DrainAsync(worker);
        Assert.Contains("registered docs/a.pdf application/pdf", hook.Events);
        Assert.Empty(await new StorageHookDeadLetters(fixture.DataSource).ListAsync());
    }

    [Fact]
    public async Task With_several_nodes_each_change_is_delivered_exactly_once()
    {
        var hook = new Recorder();
        var nodes = Enumerable.Range(0, 3).Select(_ => Node(hook)).ToList();
        for (var i = 0; i < 60; i++) await nodes[i % 3].Storage.SetAsync($"docs/f{i:D2}.bin", "x/y", 0);

        var small = Fast with { BatchSize = 5 };
        var workers = Enumerable.Range(0, 3).Select(_ => Node(hook, options: small).Worker).ToArray();
        await Task.WhenAll(workers.Select(w => Task.Run(() => DrainAsync(w))));
        await DrainAsync(workers);

        Assert.Equal(60, hook.Events.Count);
        Assert.Equal(60, hook.Events.Distinct().Count());
    }

    [Fact]
    public async Task Nothing_is_delivered_for_a_change_that_did_not_happen()
    {
        var hook = new Recorder();
        var (storage, worker) = Node(hook);
        Assert.Null(await storage.UpdateMetadataAsync("docs/missing.pdf", new() { ["k"] = "v" }));
        Assert.False(await storage.DeleteAsync("docs/missing.pdf"));
        Assert.Equal(0, await PendingAsync());
    }

    [Fact]
    public async Task Deliveries_for_a_hook_this_node_does_not_have_are_left_for_others()
    {
        var (storage, _) = Node(new Recorder(), pattern: "docs/{file=**}");
        await storage.SetAsync("docs/a.pdf", "application/pdf", 0);

        var (_, otherWorker) = Node(new Recorder(), pattern: "media/{file=**}");   // a different registration
        Assert.Equal(0, await otherWorker.RunOnceAsync(default));
        Assert.Equal(1, await PendingAsync());
    }

    [Fact]
    public async Task A_cascade_delete_delivers_every_deleted_file()
    {
        var hook = new Recorder();
        var (storage, worker) = Node(hook);
        await storage.SetAsync("docs/u1/a.pdf", "application/pdf", 0);
        await storage.SetAsync("docs/u1/sub/b.pdf", "application/pdf", 0);
        await DrainAsync(worker);
        while (hook.Events.TryDequeue(out _)) { }

        await storage.DeleteAsync("docs/u1");
        await DrainAsync(worker);
        Assert.Equal(["deleted docs/u1/a.pdf", "deleted docs/u1/sub/b.pdf"], hook.Events.Order());
    }

    [Fact]
    public async Task The_hosted_worker_delivers_right_after_the_change()
    {
        var connectionString = fixture.DataSource.ConnectionString;
        if (!connectionString.Contains("Password", StringComparison.OrdinalIgnoreCase))
            connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Password = "postgres" }.ConnectionString;
        var hook = new Recorder();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWincheStorage(o =>
        {
            o.ConnectionString = connectionString;
            o.HookDelivery = new HookDeliveryOptions { PollInterval = TimeSpan.FromMinutes(1) };   // only the wake-up can be quick
            o.UseHooks(h => h.Add("docs/{file=**}", hook));
        });
        using var host = builder.Build();
        await host.InitializeWincheStorageAsync();
        await host.StartAsync();
        try
        {
            await Task.Delay(200);   // let the worker reach its wait
            await host.Services.GetRequiredService<FileStorage>().SetAsync("docs/a.pdf", "application/pdf", 0);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (hook.Events.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(["registered docs/a.pdf application/pdf"], hook.Events);
        }
        finally { await host.StopAsync(); }
    }
}
