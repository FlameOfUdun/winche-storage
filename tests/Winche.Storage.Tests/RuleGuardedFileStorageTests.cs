using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Expressions;
using Winche.Storage.Authorization;
using Winche.Storage.Models;
using Xunit;
using Winche.Values;

namespace Winche.Storage.Tests;

public class RuleGuardedFileStorageTests
{
    private static RuleGuardedFileStorage Guarded(RecordingFileStorage inner, Action<RuleSetBuilder> rules) =>
        new(inner, new RuleEngine(new StaticRuleSetRepository(RuleSetBuilder.Build(rules))), () => null);

    [Fact]
    public async Task Folders_kept_from_apps_are_refused_whatever_the_rules_say()
    {
        var inner = new RecordingFileStorage { DeleteReturns = true };
        var guarded = new RuleGuardedFileStorage(inner,
            new RuleEngine(new StaticRuleSetRepository(RuleSetBuilder.Build(rb => rb.Match("{path=**}", mb => mb.Allow(
                [RuleOperation.Get, RuleOperation.List, RuleOperation.Create, RuleOperation.Update, RuleOperation.Delete], Expr.Const(true)))))),
            () => null, keptFromApps: ["realtime"]);

        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.GetAsync("realtime/sessions/s1/a.ndjson.gz"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.SetAsync("realtime/forged", "text/plain", 1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.DeleteAsync("realtime"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.ListAsync("realtime/sessions"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.GenerateDownloadUrlAsync("realtime/x"));
        Assert.DoesNotContain(inner.Calls, c => c.Contains(":realtime"));
        Assert.True(await guarded.DeleteAsync("realtimes/x"));   // only the folder itself
        // (The top folder's listing leaves them out too, should a rule ever allow listing it: no rule matches the empty path.)
    }

    [Fact]
    public async Task Allowed_operation_delegates_to_the_inner_core()
    {
        var inner = new RecordingFileStorage { DeleteReturns = true };
        var guarded = Guarded(inner, rb =>
            rb.Match("files/{id}", mb => mb.Allow([RuleOperation.Delete], Expr.Const(true))));

        var result = await guarded.DeleteAsync("files/1");

        Assert.True(result);
        Assert.Contains("Delete:files/1", inner.Calls);
    }

    [Fact]
    public async Task Denied_operation_throws_and_never_reaches_the_inner_core()
    {
        var inner = new RecordingFileStorage();
        var guarded = Guarded(inner, _ => { }); // no rules => default-deny

        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.DeleteAsync("files/1"));

        Assert.DoesNotContain("Delete:files/1", inner.Calls);
    }
    [Fact]
    public async Task A_create_rule_sees_the_incoming_file_as_request_resource()
    {
        var inner = new RecordingFileStorage();
        var guarded = Guarded(inner, rb =>
            rb.Match("files/{id}", mb => mb.Allow([RuleOperation.Create],
                Expr.RequestResource("sizeBytes").Le(Expr.Const(100))
                    .And(Expr.RequestResource("directory").Eq(Expr.Value(new StringValue("files")))))));

        await guarded.SetAsync("files/small", "text/plain", 100);
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.SetAsync("files/large", "text/plain", 101));

        Assert.Contains(inner.Calls, c => c.Contains("files/small"));
        Assert.DoesNotContain(inner.Calls, c => c == "Set:files/large");
    }

    private static FileRecord Pending(string path, long size) => new()
    {
        Id = path.Split('/')[^1], Directory = path[..path.LastIndexOf('/')], Path = path, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        MetaData = [], Version = 1, MimeType = "text/plain", SizeBytes = size, UploadStatus = UploadStatus.Pending,
    };

    [Fact]
    public async Task Uploading_a_registered_file_is_checked_with_that_file_as_request_resource()
    {
        // A write rule on the size, as one would write it: the upload link of a small registered file is given.
        var inner = new RecordingFileStorage { GetReturns = Pending("files/small", 100) };
        var guarded = Guarded(inner, rb =>
            rb.Match("files/{id}", mb => mb.Allow([RuleOperation.Create, RuleOperation.Update], Expr.RequestResource("sizeBytes").Le(Expr.Const(100)))));
        // Allowed: it reaches the store (which here doesn't make links).
        await Assert.ThrowsAsync<NotImplementedException>(() => guarded.GenerateUploadUrlAsync("files/small"));
        await Assert.ThrowsAnyAsync<Exception>(() => guarded.ConfirmUploadAsync("files/small")).ContinueWith(t => Assert.IsNotType<AccessDeniedException>(t.Result));
        inner.GetReturns = Pending("files/large", 101);
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.GenerateUploadUrlAsync("files/large"));
    }

    [Fact]
    public async Task Rules_may_say_size_and_contentType_for_sizeBytes_and_mimeType()
    {
        var inner = new RecordingFileStorage();
        var guarded = Guarded(inner, rb =>
            rb.Match("files/{id}", mb => mb.Allow([RuleOperation.Create],
                Expr.RequestResource("size").Le(Expr.Const(100)).And(Expr.RequestResource("contentType").Eq(Expr.Value(new StringValue("text/plain")))))));
        await guarded.SetAsync("files/a", "text/plain", 100);
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.SetAsync("files/b", "text/plain", 101));
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.SetAsync("files/c", "image/png", 10));
    }
}
