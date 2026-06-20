# Isolate the Rules Engine per Package (Winche.Database / Winche.Storage)

**Date:** 2026-06-20
**Repos affected:** `WincheDatabase`, `WincheStorage` (this spec is mirrored in both). `Winche.Rules` is **not** changed.

## Problem

When a host registers both `AddWincheDatabase(...)` and `AddWincheStorage(...)` in the same
`IServiceCollection`, their access rules leak into each other.

Both packages call `Winche.Rules`' `AddWincheRules(...)`, which registers a single un-keyed
`RuleEngine` via `TryAddSingleton`. That engine's factory merges **every** `RuleSet` in the
container:

```csharp
services.TryAddSingleton(sp => new RuleEngine(RuleSet.Merge(sp.GetServices<RuleSet>()), Comparer));
```

Each package's `UseRules(...)` pushes its ruleset into that same shared pool
(`Services.AddSingleton(ruleset)`). As a result:

- There is **one** `RuleEngine` shared by all three guards (`RulesWriteAuthorizer`,
  `RuleGuardedDocumentDatabase`, `RuleGuardedFileStorage`).
- It is built from the **union** of database rulesets and storage rulesets. A path allowed by a
  storage rule can be evaluated by the database guard and vice versa.
- Because the engine is `TryAddSingleton` (first-wins) and `AddWincheDatabase` is typically
  registered first, the shared engine uses `WincheRuleValueComparer.Instance`. Storage's guard
  therefore runs on that comparer today even though `AddWincheStorage` never set one (a latent
  bug masked by the merge).

### Not a collision

`IRuleClaimsAccessor` and `ISchemaManager` *look* shared but are **per-package types in separate
namespaces** (`Winche.Database.Authorization.IRuleClaimsAccessor` vs
`Winche.Storage.Authorization.IRuleClaimsAccessor`; likewise `ISchemaManager`). Distinct .NET
types resolve independently — no change needed. The keyed `NpgsqlDataSource` registrations also
already use distinct keys (`"WincheDatabase"` vs `"WincheStorage"`).

So the only real fix is isolating `RuleEngine` + the `RuleSet` pool.

## Goal

Each package builds its **own** `RuleEngine` from **only its own** rulesets, and its guards use
that engine. `RuleEngine` and `RuleSet` are no longer placed in the shared DI container at all, so
cross-wiring is structurally impossible.

## Design: build-and-inject locally

For **each** package (`WincheDatabase`, `WincheStorage`):

1. **Accumulate rulesets on the options object.** Add `internal List<RuleSet> Rulesets { get; } = [];`
   to `WincheDatabaseOptions` / `WincheStorageOptions`. Change both `UseRules(...)` overloads to
   `Rulesets.Add(ruleset)` instead of `Services.AddSingleton(ruleset)`.

2. **Stop calling `AddWincheRules(...)`.** Remove it from each package's
   `ServiceCollectionExtensions`.

3. **Build the engine once, after `configure(options)`** runs:

   ```csharp
   var ruleEngine = new RuleEngine(RuleSet.Merge(options.Rulesets), WincheRuleValueComparer.Instance);
   ```

   - Both packages use `WincheRuleValueComparer.Instance` (Storage matches Database, preserving
     current runtime behavior).
   - Empty `Rulesets` → `RuleSet.Merge([])` is empty → default-deny. The old explicit "deny-all
     seed" is no longer needed.

4. **Inject the instance directly into the guard registrations**, replacing
   `sp.GetRequiredService<RuleEngine>()` with the captured `ruleEngine` local:
   - Database: `RulesWriteAuthorizer`, `RuleGuardedDocumentDatabase`.
   - Storage: `RuleGuardedFileStorage`.

`Winche.Rules` is untouched; `AddWincheRules` remains available for standalone use.

## Files changed

**WincheDatabase**
- `src/Winche.Database/DependencyInjection/WincheDatabaseOptions.cs` — add `Rulesets`; both
  `UseRules` overloads append to it.
- `src/Winche.Database/DependencyInjection/ServiceCollectionExtensions.cs` — remove
  `AddWincheRules(...)`; build `ruleEngine`; capture it in the `RulesWriteAuthorizer` and
  `RuleGuardedDocumentDatabase` factories.

**WincheStorage**
- `src/Winche.Storage/DependencyInjection/WincheStorageOptions.cs` — add `Rulesets`; both
  `UseRules` overloads append to it.
- `src/Winche.Storage/DependencyInjection/ServiceCollectionExtensions.cs` — remove
  `AddWincheRules(...)`; build `ruleEngine` with `WincheRuleValueComparer.Instance`; capture it in
  the `RuleGuardedFileStorage` factory.

## Alternatives considered

- **Keyed `RuleEngine` in `Winche.Rules`** (`AddWincheRules(key, ...)` + `[FromKeyedServices]` in
  guards). Rejected: changes the `Winche.Rules` public API across all three libraries, keeps the
  engine resolvable/clobberable in DI, and relies on magic-string keys.
- **Package-local wrapper type** (e.g. `DocumentRuleEngine`/`FileRuleEngine`). Rejected as
  unnecessary: injecting the built instance directly into guard closures already achieves full
  isolation without a new type.

## Testing

- Existing `StorageRuleTests` (`new RuleEngine(ruleset)`) is unaffected.
- Add an isolation test: register both packages in one container with disjoint rulesets (a
  database-only-allowed path and a storage-only-allowed path), then assert the database guard
  denies the storage path and the storage guard denies the database path.
- Add a default-deny test: register a package with no `UseRules` and assert access is denied.

## Impact on other consumers

Consumers like `WincheConsole` that call `UseRules` separately for database and storage are
currently affected by the same leak; this change fixes them with no API change on their side
(`UseRules` / `MapClaims` signatures are unchanged).
