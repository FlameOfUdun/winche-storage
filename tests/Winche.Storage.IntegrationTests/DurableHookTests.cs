using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Abstraction;
using Winche.Storage.Models;
using Winche.Storage.Services;
using Winche.Core.Feeds;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>File hooks consume storage's feed (Winche.Core): durable, in order, retried, dead-lettered, once across nodes.</summary>
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

    private static readonly HookDeliveryOptions Fast = new();

    private sealed record Node(FileStorage Storage, WincheFeedRunner<FeedEvent> Worker, StorageHooks Hooks);

    /// <summary>A node with <paramref name="hook"/> on <paramref name="pattern"/>, started (its hook tracked) as a host starts.</summary>
    private async Task<Node> StartAsync(FileStoreHook hook, string pattern = "docs/{file=**}", HookDeliveryOptions? options = null)
    {
        var registration = new HookRegistration(pattern, hook);
        var dispatcher = new HookInvocationDispatcher([registration]);
        var hooks = new StorageHooks(fixture.DataSource, dispatcher);
        await hooks.TrackAsync();
        var consumer = StorageFeed.ConsumerOf(registration);
        return new Node(new FileStorage(fixture.DataSource, new FakeArchive(), dispatcher),
            StorageHookService.Runner(fixture.DataSource, consumer, registration, options ?? Fast, NullLogger.Instance), hooks);
    }

    private static async Task DrainAsync(params WincheFeedRunner<FeedEvent>[] workers)
    {
        for (var i = 0; i < 50; i++)
        {
            var ran = 0;
            foreach (var w in workers) ran += await w.DeliverPendingAsync();
            if (ran == 0) return;
        }
    }

    private async Task<long> FeedRowsAsync()
    {
        await using var cmd = fixture.DataSource.CreateCommand("SELECT count(*) FROM winche_files_feed");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Every_file_change_reaches_the_hook_in_order()
    {
        var hook = new Recorder();
        var node = await StartAsync(hook);
        var storage = node.Storage;
        await storage.SetAsync("docs/a.pdf", "application/pdf", 0);
        await storage.ConfirmUploadAsync("docs/a.pdf");
        await storage.UpdateMetadataAsync("docs/a.pdf", new() { ["k"] = "v" });
        await storage.SetAsync("other/b.pdf", "application/pdf", 0);   // pattern doesn't match
        await storage.DeleteAsync("docs/a.pdf");

        await DrainAsync(node.Worker);

        Assert.Equal(["registered docs/a.pdf application/pdf", "confirmed docs/a.pdf etag-single", "metadata docs/a.pdf v", "deleted docs/a.pdf"],
            hook.Events);
        Assert.Null((await node.Hooks.StatusAsync()).OldestPending);   // nothing left to take
    }

    [Fact]
    public async Task A_change_made_while_no_worker_runs_is_delivered_after_a_restart()
    {
        var node = await StartAsync(new Recorder());
        await node.Storage.SetAsync("docs/a.pdf", "application/pdf", 0);   // the process "dies" here

        var after = new Recorder();
        var restarted = await StartAsync(after);   // same hook type and pattern: the same hook
        await DrainAsync(restarted.Worker);
        Assert.Equal(["registered docs/a.pdf application/pdf"], after.Events);
    }

    [Fact]
    public async Task A_failing_change_is_retried_and_holds_that_hook_back()
    {
        var aFailures = 2;
        var hook = new Recorder { FailWhen = e => e.Contains("a.pdf") && Interlocked.Decrement(ref aFailures) >= 0 };
        var node = await StartAsync(hook);
        await node.Storage.SetAsync("docs/a.pdf", "application/pdf", 0);
        await node.Storage.ConfirmUploadAsync("docs/a.pdf");
        await node.Storage.SetAsync("docs/b.pdf", "text/plain", 0);

        Assert.Equal(0, await node.Worker.DeliverPendingAsync());   // a's registered fails; nothing after it goes
        Assert.Equal(0, await node.Worker.DeliverPendingAsync());   // fails again
        Assert.Empty(hook.Events);
        Assert.Equal(1, (await node.Hooks.StatusAsync()).Failing);

        await DrainAsync(node.Worker);
        Assert.Equal(["registered docs/a.pdf application/pdf", "confirmed docs/a.pdf etag-single", "registered docs/b.pdf text/plain"], hook.Events);
    }

    [Fact]
    public async Task A_delivery_that_keeps_failing_becomes_a_dead_letter_and_can_be_retried()
    {
        var hook = new Recorder { FailWhen = e => e.StartsWith("registered") };
        var node = await StartAsync(hook, options: Fast with { MaxRetryAge = TimeSpan.Zero });
        await node.Storage.SetAsync("docs/a.pdf", "application/pdf", 0);
        await node.Storage.ConfirmUploadAsync("docs/a.pdf");

        await DrainAsync(node.Worker);
        var dead = Assert.Single(await node.Hooks.DeadLettersAsync());
        Assert.Equal(("registered", "docs/a.pdf"), ((string?)dead.Record["type"], (string?)dead.Record["path"]));
        Assert.Contains("boom", dead.Error);
        Assert.Equal(["confirmed docs/a.pdf etag-single"], hook.Events);   // a dead letter doesn't block later changes
        var status = await node.Hooks.StatusAsync();
        Assert.Equal((null, 1L), (status.OldestPending, status.DeadLetters));   // what the health check reports

        hook.FailWhen = null;
        Assert.Equal(new(1, 0), await node.Hooks.RetryAsync());
        Assert.Contains("registered docs/a.pdf application/pdf", hook.Events);
        Assert.Empty(await node.Hooks.DeadLettersAsync());
    }

    [Fact]
    public async Task With_several_nodes_each_change_is_delivered_exactly_once()
    {
        var hook = new Recorder();
        var nodes = new List<Node>();
        for (var i = 0; i < 3; i++) nodes.Add(await StartAsync(hook, options: Fast with { BatchSize = 5 }));
        for (var i = 0; i < 60; i++) await nodes[i % 3].Storage.SetAsync($"docs/f{i:D2}.bin", "x/y", 0);

        // Every node runs the hook's consumer; the lease lets one deliver at a time.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var runs = nodes.Select(n => n.Worker.RunAsync(ct => Task.Delay(20, ct), stop.Token)).ToList();
        while (hook.Events.Count < 60 && !stop.IsCancellationRequested) await Task.Delay(20);
        await stop.CancelAsync();
        await Task.WhenAll(runs);

        Assert.Equal(60, hook.Events.Count);
        Assert.Equal(60, hook.Events.Distinct().Count());
    }

    [Fact]
    public async Task Nothing_is_recorded_for_a_change_that_did_not_happen()
    {
        var node = await StartAsync(new Recorder());
        Assert.Null(await node.Storage.UpdateMetadataAsync("docs/missing.pdf", new() { ["k"] = "v" }));
        Assert.False(await node.Storage.DeleteAsync("docs/missing.pdf"));
        Assert.Equal(0, await FeedRowsAsync());
    }

    [Fact]
    public async Task Changes_for_a_hook_this_node_does_not_have_are_kept_for_the_nodes_that_do()
    {
        var docsHook = new Recorder();
        var docs = await StartAsync(docsHook, pattern: "docs/{file=**}");
        await docs.Storage.SetAsync("docs/a.pdf", "application/pdf", 0);

        var media = await StartAsync(new Recorder(), pattern: "media/{file=**}");   // a different hook, added after the change
        Assert.Equal(0, await media.Worker.DeliverPendingAsync());   // starts from when it was added
        Assert.NotNull((await docs.Hooks.StatusAsync()).OldestPending);   // still waiting for the docs hook
        await DrainAsync(docs.Worker);
        Assert.Equal(["registered docs/a.pdf application/pdf"], docsHook.Events);
    }

    [Fact]
    public async Task A_cascade_delete_delivers_every_deleted_file()
    {
        var hook = new Recorder();
        var node = await StartAsync(hook);
        await node.Storage.SetAsync("docs/u1/a.pdf", "application/pdf", 0);
        await node.Storage.SetAsync("docs/u1/sub/b.pdf", "application/pdf", 0);
        await DrainAsync(node.Worker);
        while (hook.Events.TryDequeue(out _)) { }

        await node.Storage.DeleteAsync("docs/u1");
        await DrainAsync(node.Worker);
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
