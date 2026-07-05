using Microsoft.Extensions.DependencyInjection;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Expressions;
using Winche.Storage.Constants;
using Winche.Storage.DependencyInjection;
using Xunit;

namespace Winche.Storage.Tests;

public class RuleEngineIsolationTests
{
    private static ServiceProvider BuildProvider(Action<WincheStorageOptions>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddWincheStorage(o =>
        {
            // A well-formed connection string is enough; nothing connects during resolution.
            o.ConnectionString = "Host=localhost;Port=5432;Database=test;Username=test;Password=test";
            extra?.Invoke(o);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Keyed_engine_is_built_from_this_packages_own_rules()
    {
        using var provider = BuildProvider(o => o.UseRules(rb =>
            rb.Match("files/{id}", mb => mb.Allow([RuleOperation.Get], Expr.Const(true)))));

        var engine = provider.GetRequiredKeyedService<RuleEngine>(WincheStorageKeys.RULE_ENGINE_KEY);

        Assert.True(await engine.AllowsAsync(RuleOperation.Get, "files/1", new RuleRequest()));
        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "other/1", new RuleRequest()));
    }

    [Fact]
    public async Task With_no_UseRules_access_is_default_deny()
    {
        using var provider = BuildProvider();

        var engine = provider.GetRequiredKeyedService<RuleEngine>(WincheStorageKeys.RULE_ENGINE_KEY);

        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "files/1", new RuleRequest()));
    }

    [Fact]
    public async Task Rules_can_be_hot_swapped_midstream_through_the_keyed_engine()
    {
        // Start with a rule that grants get on files/* only.
        using var provider = BuildProvider(o => o.UseRules(rb =>
            rb.Match("files/{id}", mb => mb.Allow([RuleOperation.Get], Expr.Const(true)))));

        var engine = provider.GetRequiredKeyedService<RuleEngine>(WincheStorageKeys.RULE_ENGINE_KEY);

        // Before the swap: files/* allowed, docs/* denied.
        Assert.True(await engine.AllowsAsync(RuleOperation.Get, "files/1", new RuleRequest()));
        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));

        // Hot-swap the ruleset through the mutable write side registered under the SAME key —
        // same engine instance, no rebuild. The new rules flip the grant from files/* to docs/*.
        var repo = provider.GetRequiredKeyedService<IMutableRuleSetRepository>(WincheStorageKeys.RULE_ENGINE_KEY);
        repo.Update(RuleSetBuilder.Build(rb =>
            rb.Match("docs/{id}", mb => mb.Allow([RuleOperation.Get], Expr.Const(true)))));

        // After the swap: the very same engine observes the new rules mid-flight.
        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "files/1", new RuleRequest()));
        Assert.True(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));
    }
}
