using Winche.Rules;
using Winche.Rules.Expressions;
using Winche.Storage.Authorization;
using Xunit;

namespace Winche.Storage.Tests;

public class RuleGuardedFileStorageTests
{
    private static RuleGuardedFileStorage Guarded(RecordingFileStorage inner, Action<RulesetBuilder> rules) =>
        new(inner, new RuleEngine(RulesetBuilder.Build(rules)), () => null);

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
}
