# Per-Package Rules Engine Isolation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `Winche.Database` and `Winche.Storage` each their own isolated `RuleEngine` so their access rules never merge when both are registered in the same DI container.

**Architecture:** Each package accumulates its `UseRules` rulesets on its options object (out of DI), builds one `RuleEngine` from only its own rulesets after `configure` runs, registers it as a package-keyed singleton (`AddKeyedSingleton(RULE_ENGINE_KEY, engine)`), and resolves it in its guards via `GetRequiredKeyedService<RuleEngine>(RULE_ENGINE_KEY)`. The shared, un-keyed `AddWincheRules(...)` call is removed from both packages. `Winche.Rules` is **not** changed.

**Tech Stack:** .NET 10, C#, Microsoft.Extensions.DependencyInjection (keyed services), xUnit. Two independent git repos: `WincheDatabase` and `WincheStorage`.

**Spec:** `docs/superpowers/specs/2026-06-20-isolate-rule-engine-per-package-design.md` (mirrored in both repos).

**Cross-repo note:** The two repos are independent (separate git history). Group A tasks run in the
`WincheStorage` repo; Group B tasks run in the `WincheDatabase` repo. They have no ordering
dependency on each other and can be done in either order or in parallel. All commands below assume
the working directory is the repo root for that group.

**Out of scope:** Publishing new NuGet versions and bumping the consuming app
(`SmartAirway`) to them. The app uses `UseRules` whose signature is unchanged, so it needs no code
change — only a package version bump once these libraries are republished.

---

## Group A — WincheStorage repo

Repo root: `WincheStorage/`. Storage's guard (`RuleGuardedFileStorage`) currently resolves the
shared un-keyed `RuleEngine`; `UseRules` registers rulesets into the shared DI pool; the engine is
created by `AddWincheRules(o => o.WithRuleset(_ => { }))` with no comparer.

### Task A1: Expose internals to the test project

The new test resolves the internal `ServiceKeys.RULE_ENGINE_KEY`. `Winche.Storage` does not yet
expose internals to its test assembly (unlike `Winche.Database`).

**Files:**
- Modify: `src/Winche.Storage/Winche.Storage.csproj`

- [ ] **Step 1: Add an `InternalsVisibleTo` item group**

In `src/Winche.Storage/Winche.Storage.csproj`, add this `ItemGroup` directly after the closing
`</PropertyGroup>` (before the existing package-reference `ItemGroup`):

```xml
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
      <_Parameter1>Winche.Storage.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
```

- [ ] **Step 2: Verify it still builds**

Run: `dotnet build src/Winche.Storage/Winche.Storage.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Winche.Storage/Winche.Storage.csproj
git commit -m "build: expose Winche.Storage internals to its test project"
```

### Task A2: Add the rule-engine service key

**Files:**
- Modify: `src/Winche.Storage/Constants/ServiceKeys.cs`

- [ ] **Step 1: Add the constant**

Replace the body of `src/Winche.Storage/Constants/ServiceKeys.cs` with:

```csharp
namespace Winche.Storage.Constants;

internal static class ServiceKeys
{
    public const string DATA_SOURCE_KEY = "WincheStorage";

    /// <summary>
    /// The keyed-service key under which this package's isolated <c>RuleEngine</c> is registered.
    /// Distinct from any other package's engine so rulesets never merge.
    /// </summary>
    public const string RULE_ENGINE_KEY = "WincheStorage.RuleEngine";
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build src/Winche.Storage/Winche.Storage.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Winche.Storage/Constants/ServiceKeys.cs
git commit -m "feat: add RULE_ENGINE_KEY to Winche.Storage service keys"
```

### Task A3: Accumulate rulesets on the options object

`UseRules` currently pushes each ruleset into the shared DI container. Change it to collect them on
the options instance so the engine can be built from this package's rulesets alone.

**Files:**
- Modify: `src/Winche.Storage/DependencyInjection/WincheStorageOptions.cs`

- [ ] **Step 1: Add the `Rulesets` accumulator**

In `WincheStorageOptions`, add this property immediately after the `ConnectionString` property
(before the first `UseRules` overload):

```csharp
    /// <summary>
    /// Rulesets registered via <see cref="UseRules(RuleSet)"/>. Collected here (not in the DI
    /// container) so <c>AddWincheStorage</c> can build an engine from this package's rules only.
    /// </summary>
    internal List<RuleSet> Rulesets { get; } = [];
```

- [ ] **Step 2: Point both `UseRules` overloads at the accumulator**

Replace the two `UseRules` methods with:

```csharp
    /// <summary>
    /// Adds a <see cref="RuleSet"/> to this package's rules guard. Multiple calls accumulate — each
    /// ruleset's blocks are OR-combined. With no <c>UseRules</c> call, access is default-deny.
    /// </summary>
    public WincheStorageOptions UseRules(RuleSet ruleset)
    {
        Rulesets.Add(ruleset);
        return this;
    }

    /// <summary>Builds a <see cref="RuleSet"/> from a builder delegate and adds it to the merged set.</summary>
    public WincheStorageOptions UseRules(Action<RulesetBuilder> configure)
    {
        Rulesets.Add(RulesetBuilder.Build(configure));
        return this;
    }
```

- [ ] **Step 3: Verify it builds**

Run: `dotnet build src/Winche.Storage/Winche.Storage.csproj`
Expected: Build succeeded, 0 errors. (The engine is still created by the old `AddWincheRules` call
for now; that is replaced in Task A4.)

- [ ] **Step 4: Commit**

```bash
git add src/Winche.Storage/DependencyInjection/WincheStorageOptions.cs
git commit -m "refactor: collect Winche.Storage rulesets on the options object"
```

### Task A4: Build and key the engine; resolve it in the guard (TDD)

**Files:**
- Test: `tests/Winche.Storage.Tests/RuleEngineIsolationTests.cs`
- Modify: `src/Winche.Storage/DependencyInjection/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Winche.Storage.Tests/RuleEngineIsolationTests.cs`:

```csharp
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

        var engine = provider.GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY);

        Assert.True(await engine.AllowsAsync(RuleOperation.Get, "files/1", new RuleRequest()));
        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "other/1", new RuleRequest()));
    }

    [Fact]
    public async Task With_no_UseRules_access_is_default_deny()
    {
        using var provider = BuildProvider();

        var engine = provider.GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY);

        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "files/1", new RuleRequest()));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Winche.Storage.Tests/Winche.Storage.Tests.csproj --filter "FullyQualifiedName~RuleEngineIsolationTests"`
Expected: FAIL — `GetRequiredKeyedService<RuleEngine>(...)` throws because the engine is currently
registered un-keyed (via `AddWincheRules`), so no service exists for that key.

- [ ] **Step 3: Replace the shared registration with a keyed, package-owned engine**

In `src/Winche.Storage/DependencyInjection/ServiceCollectionExtensions.cs`, replace this block:

```csharp
        // Winche.Rules guard: merges every RuleSet from UseRules() plus this deny-all seed.
        services.AddWincheRules(o => o.WithRuleset(_ => { }));

        // Guarded-by-default: IFileStorage resolves to the rules guard. The concrete FileStorage is the
        // unguarded core — inject it directly for trusted server-side callers that have no caller claims.
        services.AddSingleton<FileStorage>();
        services.AddSingleton<RuleGuardedFileStorage>(sp =>
            new RuleGuardedFileStorage(
                sp.GetRequiredService<FileStorage>(),
                sp.GetRequiredService<RuleEngine>(),
                () => sp.GetRequiredService<IRuleClaimsAccessor>().GetClaims()));
        services.AddSingleton<IFileStorage>(sp =>
            sp.GetRequiredService<RuleGuardedFileStorage>());
```

with:

```csharp
        // This package owns an isolated rules engine, registered under a package-specific key so the
        // Storage engine and the Database engine never merge. Built from this package's own UseRules
        // rulesets (empty => deny-all).
        var ruleEngine = new RuleEngine(RuleSet.Merge(options.Rulesets), WincheRuleValueComparer.Instance);
        services.AddKeyedSingleton(ServiceKeys.RULE_ENGINE_KEY, ruleEngine);

        // Guarded-by-default: IFileStorage resolves to the rules guard. The concrete FileStorage is the
        // unguarded core — inject it directly for trusted server-side callers that have no caller claims.
        services.AddSingleton<FileStorage>();
        services.AddSingleton<RuleGuardedFileStorage>(sp =>
            new RuleGuardedFileStorage(
                sp.GetRequiredService<FileStorage>(),
                sp.GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY),
                () => sp.GetRequiredService<IRuleClaimsAccessor>().GetClaims()));
        services.AddSingleton<IFileStorage>(sp =>
            sp.GetRequiredService<RuleGuardedFileStorage>());
```

- [ ] **Step 4: Remove the now-unused `Winche.Rules.DependencyInjection` using**

At the top of the same file, delete the line:

```csharp
using Winche.Rules.DependencyInjection;
```

(`AddWincheRules` / `WithRuleset` are no longer referenced. Leave `using Winche.Rules;` — it
provides `RuleEngine`, `RuleSet`, and `WincheRuleValueComparer`.)

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Winche.Storage.Tests/Winche.Storage.Tests.csproj --filter "FullyQualifiedName~RuleEngineIsolationTests"`
Expected: PASS — both tests green.

- [ ] **Step 6: Commit**

```bash
git add src/Winche.Storage/DependencyInjection/ServiceCollectionExtensions.cs tests/Winche.Storage.Tests/RuleEngineIsolationTests.cs
git commit -m "feat: isolate Winche.Storage rules in a package-keyed engine"
```

### Task A5: Run the full storage test suite

- [ ] **Step 1: Run all storage tests**

Run: `dotnet test tests/Winche.Storage.Tests/Winche.Storage.Tests.csproj`
Expected: PASS — all tests green, including the existing `RegistrationTests` (which still resolve
`IFileStorage` to `RuleGuardedFileStorage`). If any test resolved the old un-keyed `RuleEngine`,
fix it to use `GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY)`.

- [ ] **Step 2: Commit (only if a fix was needed in Step 1)**

```bash
git add -A
git commit -m "test: update storage tests for keyed rule engine"
```

---

## Group B — WincheDatabase repo

Repo root: `WincheDatabase/`. Database's guards (`RulesWriteAuthorizer`,
`RuleGuardedDocumentDatabase`) currently resolve the shared un-keyed `RuleEngine`, created by
`AddWincheRules(o => o.WithComparer(WincheRuleValueComparer.Instance).WithRuleset(_ => { }))`.
`Winche.Database` already exposes internals to `Winche.Database.Tests`, so no csproj change is
needed.

### Task B1: Add the rule-engine service key

**Files:**
- Modify: `src/Winche.Database/Constants/ServiceKeys.cs`

- [ ] **Step 1: Add the constant**

Replace the body of `src/Winche.Database/Constants/ServiceKeys.cs` with:

```csharp
namespace Winche.Database.Constants;

/// <summary>
/// Contains constant keys used for service registration in the Winche Database system.
/// </summary>
internal sealed class ServiceKeys
{
    /// <summary>
    /// The keyed-service key under which the store's NpgsqlDataSource is registered.
    /// </summary>
    public const string DATA_SOURCE_KEY = "WincheDatabase";

    /// <summary>
    /// The keyed-service key under which this package's isolated <c>RuleEngine</c> is registered.
    /// Distinct from any other package's engine so rulesets never merge.
    /// </summary>
    public const string RULE_ENGINE_KEY = "WincheDatabase.RuleEngine";
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build src/Winche.Database/Winche.Database.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Winche.Database/Constants/ServiceKeys.cs
git commit -m "feat: add RULE_ENGINE_KEY to Winche.Database service keys"
```

### Task B2: Accumulate rulesets on the options object

**Files:**
- Modify: `src/Winche.Database/DependencyInjection/WincheDatabaseOptions.cs`

- [ ] **Step 1: Add the `Rulesets` accumulator**

In `WincheDatabaseOptions`, add this property immediately after the `ConnectionString` property
(before `TransactionConfig`):

```csharp
    /// <summary>
    /// Rulesets registered via <see cref="UseRules(RuleSet)"/>. Collected here (not in the DI
    /// container) so <c>AddWincheDatabase</c> can build an engine from this package's rules only.
    /// </summary>
    internal List<RuleSet> Rulesets { get; } = [];
```

- [ ] **Step 2: Point both `UseRules` overloads at the accumulator**

Replace the two `UseRules` methods with (doc comments preserved, bodies changed):

```csharp
    /// <summary>
    /// Adds a <see cref="RuleSet"/> to this package's rules guard
    /// (<see cref="Authorization.RuleGuardedDocumentDatabase"/>). Multiple <c>UseRules</c> calls
    /// accumulate: each call's blocks are OR-combined with all others. With no <c>UseRules</c> call,
    /// access is default-deny.
    /// </summary>
    public WincheDatabaseOptions UseRules(RuleSet ruleset)
    {
        Rulesets.Add(ruleset);
        return this;
    }

    /// <summary>
    /// Builds a <see cref="RuleSet"/> from a <see cref="RulesetBuilder"/> delegate and adds it to the
    /// merged set. Multiple <c>UseRules</c> calls accumulate — each registered ruleset's blocks are
    /// OR-combined with all others. With no <c>UseRules</c> call, access is default-deny.
    /// </summary>
    public WincheDatabaseOptions UseRules(Action<RulesetBuilder> configure)
    {
        Rulesets.Add(RulesetBuilder.Build(configure));
        return this;
    }
```

- [ ] **Step 3: Verify it builds**

Run: `dotnet build src/Winche.Database/Winche.Database.csproj`
Expected: Build succeeded, 0 errors. (The engine is still created by the old `AddWincheRules` call
for now; that is replaced in Task B3.)

- [ ] **Step 4: Commit**

```bash
git add src/Winche.Database/DependencyInjection/WincheDatabaseOptions.cs
git commit -m "refactor: collect Winche.Database rulesets on the options object"
```

### Task B3: Build and key the engine; resolve it in the guards (TDD)

**Files:**
- Test: `tests/Winche.Database.Tests/RuleEngineIsolationTests.cs`
- Modify: `src/Winche.Database/DependencyInjection/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Winche.Database.Tests/RuleEngineIsolationTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Expressions;

namespace Winche.Database.Tests;

public class RuleEngineIsolationTests
{
    private static ServiceProvider BuildProvider(Action<WincheDatabaseOptions>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddWincheDatabase(o =>
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
            rb.Match("docs/{id}", mb => mb.Allow([RuleOperation.Get], Expr.Const(true)))));

        var engine = provider.GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY);

        Assert.True(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));
        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "files/1", new RuleRequest()));
    }

    [Fact]
    public async Task With_no_UseRules_access_is_default_deny()
    {
        using var provider = BuildProvider();

        var engine = provider.GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY);

        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));
    }
}
```

(The `Winche.Database.Tests` project has a global `using Xunit;`, so `[Fact]` and `Assert` need no
explicit import.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Winche.Database.Tests/Winche.Database.Tests.csproj --filter "FullyQualifiedName~RuleEngineIsolationTests"`
Expected: FAIL — `GetRequiredKeyedService<RuleEngine>(...)` throws because the engine is currently
registered un-keyed (via `AddWincheRules`).

- [ ] **Step 3: Replace the shared registration with a keyed, package-owned engine**

In `src/Winche.Database/DependencyInjection/ServiceCollectionExtensions.cs`, replace this block:

```csharp
        // ── Winche.Rules guard ─────────────────────────────────────────────────
        // Register the engine via Winche.Rules DI. It merges every RuleSet registered by UseRules()
        // (plus the default deny-all seed below) and uses the database's engine-faithful comparer.
        services.AddWincheRules(o => o
            .WithComparer(WincheRuleValueComparer.Instance)
            .WithRuleset(_ => { })
        );                                     // default deny-all seed

        services.AddSingleton<IWriteAuthorizer>(sp => new RulesWriteAuthorizer(
            sp.GetRequiredService<RuleEngine>(),
            sp.GetRequiredService<IRuleClaimsAccessor>())
        );
```

with:

```csharp
        // ── Rules guard ─────────────────────────────────────────────────────────
        // This package owns an isolated rules engine, registered under a package-specific key so the
        // Database engine and the Storage engine never merge. Built from this package's own UseRules
        // rulesets (empty => deny-all), using the engine-faithful comparer.
        var ruleEngine = new RuleEngine(RuleSet.Merge(options.Rulesets), WincheRuleValueComparer.Instance);
        services.AddKeyedSingleton(ServiceKeys.RULE_ENGINE_KEY, ruleEngine);

        services.AddSingleton<IWriteAuthorizer>(sp => new RulesWriteAuthorizer(
            sp.GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY),
            sp.GetRequiredService<IRuleClaimsAccessor>())
        );
```

- [ ] **Step 4: Update the read-guard registration to resolve the keyed engine**

Further down the same file, in the `RuleGuardedDocumentDatabase` registration, change the engine
resolution line from:

```csharp
                sp.GetRequiredService<RuleEngine>(),
```

to:

```csharp
                sp.GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY),
```

(This is the argument passed after the inner `DocumentDatabase` — the second `RuleEngine`
resolution in the file. There are exactly two `sp.GetRequiredService<RuleEngine>()` call sites; the
first was replaced in Step 3, this is the second.)

- [ ] **Step 5: Remove the now-unused `Winche.Rules.DependencyInjection` using**

At the top of the same file, delete the line:

```csharp
using Winche.Rules.DependencyInjection;
```

(`AddWincheRules` / `WithComparer` / `WithRuleset` are no longer referenced. Leave
`using Winche.Rules;` — it provides `RuleEngine`, `RuleSet`, and `WincheRuleValueComparer`.)

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Winche.Database.Tests/Winche.Database.Tests.csproj --filter "FullyQualifiedName~RuleEngineIsolationTests"`
Expected: PASS — both tests green.

- [ ] **Step 7: Commit**

```bash
git add src/Winche.Database/DependencyInjection/ServiceCollectionExtensions.cs tests/Winche.Database.Tests/RuleEngineIsolationTests.cs
git commit -m "feat: isolate Winche.Database rules in a package-keyed engine"
```

### Task B4: Run the full database test suite

- [ ] **Step 1: Run all database unit tests**

Run: `dotnet test tests/Winche.Database.Tests/Winche.Database.Tests.csproj`
Expected: PASS — all tests green. If any test resolved the old un-keyed `RuleEngine`, fix it to use
`GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY)`.

- [ ] **Step 2: Build the integration-test project (do not require a live DB)**

Run: `dotnet build tests/Winche.Database.IntegrationTests/Winche.Database.IntegrationTests.csproj`
Expected: Build succeeded. If it referenced the un-keyed `RuleEngine`, update it to the keyed
resolution. (Running the integration tests needs a live Postgres and is out of scope here; building
is enough to catch compile-level coupling to the old registration.)

- [ ] **Step 3: Commit (only if a fix was needed in Step 1 or 2)**

```bash
git add -A
git commit -m "test: update database tests for keyed rule engine"
```

---

## Self-review notes (already reconciled)

- **Spec coverage:** Goal (isolated per-package engine), per-package key (A2/B1), ruleset
  accumulation (A3/B2), keyed engine build + guard resolution (A4/B3), `WincheRuleValueComparer`
  for both packages (A4/B3), default-deny on empty rules (tests in A4/B3), `Winche.Rules` untouched
  (no Group for it), and the testing section (A4/A5/B3/B4) are all represented.
- **Type/name consistency:** `RULE_ENGINE_KEY`, `Rulesets`, `RuleSet.Merge`, `RuleEngine`,
  `WincheRuleValueComparer.Instance`, `GetRequiredKeyedService<RuleEngine>` are used identically
  across tasks and match the existing `Winche.Rules` API (`RuleEngine(RuleSet, IRuleValueComparer?)`,
  `RuleEngine.AllowsAsync(RuleOperation, string, RuleRequest, CancellationToken)`,
  `RulesetBuilder.Build`, `MatchBuilder.Allow(IEnumerable<RuleOperation>, RuleExpression)`,
  `Expr.Const(bool)`).
- **No placeholders:** every code step contains complete code.
