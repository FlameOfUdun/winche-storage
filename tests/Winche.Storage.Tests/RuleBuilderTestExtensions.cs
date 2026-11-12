using Winche.Rules;
using Winche.Storage.DependencyInjection;

namespace Winche.Storage.Tests;

/// <summary>
/// Apps write rule text; test fixtures still build rulesets in C# with Winche.Rules' internal builder
/// (visible to this assembly). This keeps their <c>UseRules(r =&gt; ...)</c> calls working.
/// </summary>
internal static class RuleBuilderTestExtensions
{
    public static WincheStorageOptions UseRules(this WincheStorageOptions options, Action<RuleSetBuilder> configure) =>
        options.UseRules(RuleSetBuilder.Build(configure));
}
