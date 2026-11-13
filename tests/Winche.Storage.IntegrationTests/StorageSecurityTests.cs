using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Rules.Text;
using Winche.Storage.Authorization;
using Winche.Storage.Models;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>Each attack the step-1 review found, against real Postgres and real rules.</summary>
public class StorageSecurityTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string OwnerRules = """
        match {path=**} {
          allow create: if request.auth != null && request.resource.metadata.owner == request.auth.uid;
          allow get, update, delete: if request.auth != null && resource.metadata.owner == request.auth.uid
            && (request.resource == null || request.resource.metadata.owner == resource.metadata.owner);
        }
        """;

    private (FileStorage Core, Func<string, RuleGuardedFileStorage> As) Storage(FakeArchive? archive = null)
    {
        var core = new FileStorage(fixture.DataSource, archive ?? new FakeArchive { SingleObjectSize = 4 }, new HookInvocationDispatcher([]));
        var engine = new RuleEngine(new StaticRuleSetRepository(RuleText.ParseRuleSet(OwnerRules)));
        return (core, uid => new RuleGuardedFileStorage(core, engine, () => new Dictionary<string, object?> { ["uid"] = uid }));
    }

    private static JsonObject Owner(string uid) => new() { ["owner"] = uid };

    [Fact]
    public async Task Registering_over_someone_elses_file_is_checked_as_an_update_of_it()
    {
        var (core, a) = Storage();
        await a("alice").SetAsync("shared/report.pdf", "application/pdf", 4, Owner("alice"));
        await a("alice").ConfirmUploadAsync("shared/report.pdf");

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            a("mallory").SetAsync("shared/report.pdf", "application/pdf", 4, Owner("mallory")));

        var file = await core.GetAsync("shared/report.pdf");
        Assert.Equal("alice", (string?)file!.MetaData["owner"]);
        Assert.Equal(UploadStatus.Complete, file.UploadStatus);
    }

    [Fact]
    public async Task Replacing_ones_own_file_starts_its_upload_over()
    {
        var (core, a) = Storage();
        await a("alice").SetAsync("notes/a.txt", "text/plain", 4, Owner("alice"));
        await a("alice").ConfirmUploadAsync("notes/a.txt");

        var replaced = await a("alice").SetAsync("notes/a.txt", "text/plain", 4, Owner("alice"));
        Assert.Equal(UploadStatus.Pending, replaced.UploadStatus);
        Assert.Null(replaced.ContentHash);
        Assert.Equal(2, replaced.Version);
        var exists = await Assert.ThrowsAsync<StorageException>(() =>
            a("alice").SetAsync("notes/a.txt", "text/plain", 4, Owner("alice"), FilePrecondition.IfNotExists));
        Assert.Equal(WincheErrorCode.AlreadyExists, exists.Error.Kind);
    }

    [Fact]
    public async Task A_folder_delete_needs_every_file_deletable_and_is_all_or_nothing()
    {
        var (core, a) = Storage();
        await a("alice").SetAsync("team/a.txt", "text/plain", 4, Owner("alice"));
        await a("alice").SetAsync("team/sub/b.txt", "text/plain", 4, Owner("alice"));
        await a("bob").SetAsync("team/sub/bob.txt", "text/plain", 4, Owner("bob"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => a("alice").DeleteAsync("team"));
        Assert.Equal(3, (await core.ListAsync("team")).Count() + (await core.ListAsync("team/sub")).Count());

        await a("bob").DeleteAsync("team/sub/bob.txt");
        Assert.True(await a("alice").DeleteAsync("team"));
        Assert.Empty(await core.ListAsync("team"));
        Assert.Empty(await core.ListAsync("team/sub"));
    }

    [Fact]
    public async Task Only_one_spelling_of_a_path_is_accepted()
    {
        var (core, _) = Storage();
        foreach (var bad in new[] { "/a/b", "a/b/", "a//b", "a/./b", "a/../b", "a\\b", "a/\u0001b", new string('x', 1001) })
            Assert.Equal("invalid-path", (await Assert.ThrowsAsync<StorageException>(() => core.SetAsync(bad, "text/plain", 1))).Error.Reason);
    }

    [Fact]
    public async Task Confirm_holds_the_upload_to_the_declared_size()
    {
        var (core, _) = Storage(new FakeArchive { SingleObjectSize = 1 });
        await core.SetAsync("big.bin", "application/octet-stream", 10_000_000);
        await Assert.ThrowsAsync<UploadSizeMismatchException>(() => core.ConfirmUploadAsync("big.bin"));
        Assert.Equal(UploadStatus.Pending, (await core.GetAsync("big.bin"))!.UploadStatus);
    }

    [Fact]
    public async Task A_confirm_racing_a_replacement_doesnt_complete_the_new_file()
    {
        var archive = new FakeArchive { SingleObjectSize = 4 };
        var (core, _) = Storage(archive);
        await core.SetAsync("a.txt", "text/plain", 4);
        archive.OnHead = async () => { archive.OnHead = null; await core.SetAsync("a.txt", "text/plain", 4); };

        Assert.Equal(WincheErrorCode.Aborted, (await Assert.ThrowsAsync<StorageException>(() => core.ConfirmUploadAsync("a.txt"))).Error.Kind);
        var file = await core.GetAsync("a.txt");
        Assert.Equal((UploadStatus.Pending, 2L), (file!.UploadStatus, file.Version));
    }

    [Fact]
    public async Task An_update_rule_sees_the_merged_metadata()
    {
        var (core, a) = Storage();
        await a("alice").SetAsync("a.txt", "text/plain", 4, Owner("alice"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => a("alice").UpdateMetadataAsync("a.txt", Owner("mallory")));
        var updated = await a("alice").UpdateMetadataAsync("a.txt", new JsonObject { ["title"] = "Notes" });
        Assert.Equal(("alice", "Notes"), ((string?)updated!.MetaData["owner"], (string?)updated.MetaData["title"]));
    }

    [Fact]
    public async Task Two_requests_starting_a_multipart_upload_share_one()
    {
        var archive = new FakeArchive { StartTogether = 2 };
        var (core, _) = Storage(archive);
        await core.SetAsync("big.bin", "application/octet-stream", 10_000_000);

        var links = await Task.WhenAll(core.SignPartAsync("big.bin", 1), core.SignPartAsync("big.bin", 1));

        var file = await core.GetAsync("big.bin");
        Assert.Equal(links[0].Url, links[1].Url);
        Assert.Single(archive.AbortedUploadIds);
        Assert.NotEqual(file!.UploadId, archive.AbortedUploadIds[0]);

        // A part other than the first can't start an upload: a client error, not a crash.
        await core.SetAsync("other.bin", "application/octet-stream", 10);
        Assert.Equal("no-upload", (await Assert.ThrowsAsync<StorageException>(() => core.SignPartAsync("other.bin", 2))).Error.Reason);
    }
}
