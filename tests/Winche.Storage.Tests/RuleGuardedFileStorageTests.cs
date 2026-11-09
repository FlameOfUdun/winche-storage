using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Expressions;
using Winche.Storage.Authorization;
using Xunit;

namespace Winche.Storage.Tests;

public class RuleGuardedFileStorageTests
{
    private static RuleGuardedFileStorage Guarded(RecordingFileStorage inner, Action<RuleSetBuilder> rules) =>
        new(inner, new RuleEngine(new StaticRuleSetRepository(RuleSetBuilder.Build(rules)), new DefaultRuleValueComparer()), () => null);

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
                    .And(Expr.RequestResource("directory").Eq(Expr.Value(RuleValue.String("files")))))));

        await guarded.SetAsync("files/small", "text/plain", 100);
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.SetAsync("files/large", "text/plain", 101));

        Assert.Contains(inner.Calls, c => c.Contains("files/small"));
        Assert.DoesNotContain(inner.Calls, c => c.Contains("files/large"));
    }
}
