# WincheStorage: Hook Registration + Firestore Path Patterns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mirror the WincheDatabase hook/path-pattern changes in WincheStorage: register hooks with a `UseHooks(h => h.Add<T>(path))` builder (path supplied at registration, not on the class), and make hook path matching Firestore-faithful by delegating to `Winche.Rules.Matching.PathMatcher` instead of the hand-rolled matcher.

**Architecture:** `FileStoreHook` becomes behavior-only; a new `HookRegistration(Path, FileStoreHook)` record (the storage analog of `HookRegistration` in WincheDatabase) binds a path to a hook. `HookInvocationDispatcher` resolves `IEnumerable<HookRegistration>` and matches with `PathMatcher.IsMatch`. **No database/table changes** — the table is already `winche_files` and columns stay as-is.

**Tech Stack:** C# / .NET 10, `Winche.Rules` 2.1.0 (for the public `PathMatcher`). **No test project exists** in this repo, so verification is `dotnet build` (the matching grammar itself is unit-tested upstream in Winche.Rules).

**Repo:** `C:\Users\Ehsan Rashidi\Desktop\Winche\.NET\WincheStorage` (run `dotnet` from here; .NET 10 SDK on PATH).

**Breaking change** (next major). `AddHook<T>()` and `FileStoreHook.Path` are removed; bare `**` hook patterns and zero-segment `{name=**}` matches stop working (Firestore-faithful: recursive = one-or-more, bare `*`/`**` rejected).

> **Commit note:** Commit steps are included per convention. Confirm before committing (standing rule: no commits without explicit approval).

---

## File Structure

- `src/Winche.Storage/Winche.Storage.csproj` — bump `Winche.Rules` 2.0.0 → 2.1.0 (Task 1).
- `src/Winche.Storage/Abstraction/FileStoreHook.cs` — drop `Path` (Task 2).
- `src/Winche.Storage/Abstraction/HookRegistration.cs` — new record (Task 2).
- `src/Winche.Storage/DependencyInjection/WincheStorageOptions.cs` — `AddHook` → `UseHooks` + `HookBuilder` (Task 2).
- `src/Winche.Storage/Services/HookInvocationDispatcher.cs` — consume `HookRegistration`, match via `PathMatcher` (Task 2).
- `samples/Winche.Storage.Sample/Configurations/FileUpdateHook.cs` + `samples/Winche.Storage.Sample/Program.cs` — drop `Path`, use `UseHooks` (Task 2).
- `README.md`, `Directory.Build.props` — docs + version bump (Task 3).

`IHookInvocationDispatcher` is **unchanged** (its `Readers` tuple stays `(FileStoreHook, Reader)`; `Enqueue` signature stays), so `HookInvocationProcessor` and `FileStorage` callers need no edits. The DI registration `AddSingleton<HookInvocationDispatcher>()` is unchanged — it auto-resolves the new `IEnumerable<HookRegistration>` constructor parameter.

---

## Task 1: Bump Winche.Rules to 2.1.0

The Firestore-faithful matcher uses the public `Winche.Rules.Matching.PathMatcher`, introduced in 2.1.0.

**Files:**
- Modify: `src/Winche.Storage/Winche.Storage.csproj`

- [ ] **Step 1: Update the package reference**

In `src/Winche.Storage/Winche.Storage.csproj`, change:

```xml
    <PackageReference Include="Winche.Rules" Version="2.0.0" />
```

to:

```xml
    <PackageReference Include="Winche.Rules" Version="2.1.0" />
```

- [ ] **Step 2: Restore and verify the package resolves**

Run: `dotnet restore src/Winche.Storage/Winche.Storage.csproj`
Expected: restores `Winche.Rules 2.1.0` with no `NU1102`/version errors.

- [ ] **Step 3: Confirm the public matcher is visible**

Run: `dotnet build src/Winche.Storage/Winche.Storage.csproj`
Expected: Build succeeds (the existing code still compiles; the new API is consumed in Task 2).

- [ ] **Step 4: Commit** (only if approved)

```bash
git add src/Winche.Storage/Winche.Storage.csproj
git commit -m "chore(storage): bump Winche.Rules to 2.1.0 for public PathMatcher"
```

---

## Task 2: Hook registration builder + Firestore path matching

Replace `AddHook<T>()` with `UseHooks(h => h.Add<T>(path))`, move the path off `FileStoreHook` into `HookRegistration`, and match via `PathMatcher`.

**Files:**
- Modify: `src/Winche.Storage/Abstraction/FileStoreHook.cs`
- Create: `src/Winche.Storage/Abstraction/HookRegistration.cs`
- Modify: `src/Winche.Storage/DependencyInjection/WincheStorageOptions.cs`
- Modify: `src/Winche.Storage/Services/HookInvocationDispatcher.cs`
- Modify: `samples/Winche.Storage.Sample/Configurations/FileUpdateHook.cs`
- Modify: `samples/Winche.Storage.Sample/Program.cs`

- [ ] **Step 1: Drop `Path` from `FileStoreHook`**

In `src/Winche.Storage/Abstraction/FileStoreHook.cs`, remove the `Path` property so the class is behavior-only:

```csharp
using Winche.Storage.Models;

namespace Winche.Storage.Abstraction;

/// <summary>
/// A file lifecycle hook. The path pattern selecting which files fire this hook is supplied at
/// registration time (<see cref="DependencyInjection.HookBuilder.Add{THook}(string)"/>), not on the
/// hook — so the same hook behavior can be bound to different paths.
/// </summary>
public abstract class FileStoreHook
{
    public virtual Task OnFileRegisteredAsync(FileRecord record, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnUploadConfirmedAsync(FileRecord record, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnFileDeletedAsync(string path, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnMetadataUpdatedAsync(FileRecord record, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnUploadUrlGeneratedAsync(string path, UploadSession session, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnDownloadUrlGeneratedAsync(string path, DownloadSession session, CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 2: Add the `HookRegistration` record**

Create `src/Winche.Storage/Abstraction/HookRegistration.cs`:

```csharp
namespace Winche.Storage.Abstraction;

/// <summary>
/// Binds a Firestore-style path pattern to the <see cref="FileStoreHook"/> that fires for matching
/// files. The hook supplies behavior; the path is supplied at registration time, so one hook type
/// can be bound to multiple paths.
///
/// <para>The pattern uses the rules-engine grammar: literal segments, <c>{id}</c> single-segment
/// captures, and a trailing recursive wildcard <c>{name=**}</c> (matching one or more segments). Use
/// <c>"{file=**}"</c> to match every file. Bare <c>*</c>/<c>**</c> are not valid.</para>
/// </summary>
/// <param name="Path">The path pattern selecting which files fire <paramref name="Hook"/>.</param>
/// <param name="Hook">The hook behavior invoked for matching files.</param>
public sealed record HookRegistration(string Path, FileStoreHook Hook);
```

- [ ] **Step 3: Replace `AddHook` with `UseHooks` + `HookBuilder`**

In `src/Winche.Storage/DependencyInjection/WincheStorageOptions.cs`, add `using Microsoft.Extensions.DependencyInjection.Extensions;` at the top (for `TryAddSingleton`), then replace the `AddHook<THook>()` method:

```csharp
    /// <summary>
    /// Registers file lifecycle hooks via a fluent builder, binding each hook to a Firestore-style
    /// path pattern at registration time. Multiple calls accumulate — each binding is registered as a
    /// singleton so that <c>GetServices&lt;HookRegistration&gt;()</c> returns them all at startup.
    /// </summary>
    public WincheStorageOptions UseHooks(Action<HookBuilder> configure)
    {
        var builder = new HookBuilder(Services);
        configure(builder);
        return this;
    }
```

Then add the `HookBuilder` class at the end of the file (after the closing brace of `WincheStorageOptions`, still in namespace `Winche.Storage.DependencyInjection`):

```csharp
/// <summary>
/// Fluent builder used inside <see cref="WincheStorageOptions.UseHooks(Action{HookBuilder})"/>.
/// Each <see cref="Add{THook}(string)"/> registers one <see cref="Abstraction.HookRegistration"/> as a
/// singleton so that <c>GetServices&lt;HookRegistration&gt;()</c> returns them all at startup.
/// </summary>
public sealed class HookBuilder(IServiceCollection services)
{
    /// <summary>
    /// Registers a hook <typeparamref name="THook"/> (constructed via DI, so constructor injection
    /// works) bound to <paramref name="path"/>. The same hook type may be bound to multiple paths.
    /// </summary>
    public HookBuilder Add<THook>(string path) where THook : Abstraction.FileStoreHook
    {
        services.TryAddSingleton<THook>();
        services.AddSingleton(sp => new Abstraction.HookRegistration(path, sp.GetRequiredService<THook>()));
        return this;
    }

    /// <summary>Registers a pre-constructed <paramref name="hook"/> instance bound to <paramref name="path"/>.</summary>
    public HookBuilder Add(string path, Abstraction.FileStoreHook hook)
    {
        services.AddSingleton(new Abstraction.HookRegistration(path, hook));
        return this;
    }
}
```

(`WincheStorageOptions.cs` already has `using Winche.Storage.Abstraction;`; the fully-qualified `Abstraction.` prefixes above are belt-and-suspenders and compile regardless.)

- [ ] **Step 4: Rewrite `HookInvocationDispatcher` to use registrations + `PathMatcher`**

Replace `src/Winche.Storage/Services/HookInvocationDispatcher.cs` entirely:

```csharp
using System.Threading.Channels;
using Winche.Rules.Matching;
using Winche.Storage.Abstraction;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.Services;

public sealed class HookInvocationDispatcher(
    IEnumerable<HookRegistration> hooks
) : IHookInvocationDispatcher
{
    private readonly IReadOnlyDictionary<HookRegistration, Channel<HookInvocation>> _channels =
        hooks.ToDictionary(h => h, _ => Channel.CreateUnbounded<HookInvocation>());

    public IEnumerable<(FileStoreHook Hook, ChannelReader<HookInvocation> Reader)> Readers =>
        _channels.Select(kv => (kv.Key.Hook, kv.Value.Reader));

    public void Enqueue(string path, Func<FileStoreHook, CancellationToken, Task> invoke)
    {
        foreach (var (registration, channel) in _channels)
        {
            if (!PathMatcher.IsMatch(registration.Path, path)) continue;
            channel.Writer.TryWrite(new HookInvocation(ct => invoke(registration.Hook, ct)));
        }
    }

    public void Complete()
    {
        foreach (var channel in _channels.Values)
            channel.Writer.Complete();
    }
}
```

This removes the hand-rolled `MatchesPattern` (which allowed bare `**` and zero-or-more recursive) in favor of `PathMatcher.IsMatch` (Firestore-faithful: `{id}` single-segment, trailing `{name=**}` one-or-more, rejects bare `*`/`**` and malformed patterns). `IHookInvocationDispatcher` and `HookInvocationProcessor` are unchanged because `Readers` still yields `(FileStoreHook, Reader)` and `Enqueue` keeps its signature.

- [ ] **Step 5: Update the sample hook (drop `Path`)**

In `samples/Winche.Storage.Sample/Configurations/FileUpdateHook.cs`, remove the `Path` property:

```csharp
public class FileUpdateHook : FileStoreHook
{
    public override Task OnFileRegisteredAsync(FileRecord record, CancellationToken ct)
```

(Delete the line `public override string Path => "userFiles/{userId}/**";` and the blank line after it. Leave all the `On…` overrides unchanged.)

- [ ] **Step 6: Update the sample registration (bare `**` → `{file=**}`)**

In `samples/Winche.Storage.Sample/Program.cs`, replace:

```csharp
    opts.AddHook<FileUpdateHook>();
```

with:

```csharp
    opts.UseHooks(h => h.Add<FileUpdateHook>("userFiles/{userId}/{file=**}"));
```

(The old sample bound the hook to `"userFiles/{userId}/**"` via the class `Path`; bare `**` is no longer valid, so the path moves to registration and uses the recursive capture `{file=**}`.)

- [ ] **Step 7: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeds, 0 errors. If any caller referenced `FileStoreHook.Path` or `AddHook`, fix it (search `grep -rn "AddHook\|\.Path" --include=*.cs` — only the sample/options/dispatcher should have referenced them).

- [ ] **Step 8: Commit** (only if approved)

```bash
git add -A
git commit -m "feat(storage)!: UseHooks builder with path-at-registration; Firestore-faithful hook matching"
```

---

## Task 3: README + version bump

**Files:**
- Modify: `README.md`
- Modify: `Directory.Build.props`

- [ ] **Step 1: Update the README hook registration example**

In `README.md`, replace the registration line (around line 128) `opts.AddHook<AuditHook>();` with:

```csharp
    opts.UseHooks(h => h.Add<AuditHook>("userFiles/{userId}/{file=**}"));
```

- [ ] **Step 2: Update the README options table row**

Replace the table row (around line 140):

```
| `AddHook<T>()` | Registers a `FileStoreHook` lifecycle listener. |
```

with:

```
| `UseHooks(h => h.Add<T>(path))` | Registers `FileStoreHook` lifecycle listeners, each bound to a Firestore-style path pattern. |
```

- [ ] **Step 3: Update the README Hooks section**

In the Hooks section (around lines 333–350), make these edits:
- Change the intro sentence "Hooks are matched by `Path` (the same …" to describe registration-time paths:

  > Implement `FileStoreHook` (behavior only) and register it against a path with `UseHooks(h => h.Add<T>(path))`. The path is a Firestore-style pattern (literal segments, `{id}` single-segment captures, trailing `{name=**}` recursive — one or more segments; bare `*`/`**` are not valid). The same hook type can be bound to multiple paths.

- In the `AuditHook` example, delete the `public override string Path => …;` line (behavior-only class).
- Replace the trailing `Register: \`opts.AddHook<AuditHook>()\`.` line with:

  > Register: `opts.UseHooks(h => h.Add<AuditHook>("userFiles/{userId}/{file=**}"))`.

- [ ] **Step 4: Bump the package version**

In `Directory.Build.props`, change:

```xml
    <Version>4.0.0</Version>
```

to:

```xml
    <Version>5.0.0</Version>
```

- [ ] **Step 5: Build to confirm nothing broke**

Run: `dotnet build`
Expected: Build succeeds, 0 errors.

- [ ] **Step 6: Commit** (only if approved)

```bash
git add README.md Directory.Build.props
git commit -m "docs(storage): UseHooks + Firestore path patterns; bump to 5.0.0"
```

---

## Release notes (5.0.0)

- **Hooks register with `UseHooks`, and the path moves to registration.** `AddHook<T>()` is removed and `FileStoreHook.Path` no longer exists. Replace `opts.AddHook<MyHook>()` (+ a `Path` override) with `opts.UseHooks(h => h.Add<MyHook>("userFiles/{userId}/{file=**}"))`.
- **Hook path matching is Firestore-faithful** (via `Winche.Rules` `PathMatcher`): `{id}` single-segment captures and a trailing `{name=**}` recursive wildcard matching **one or more** segments. **Bare `*`/`**` are no longer valid** and malformed patterns throw — migrate `".../**"` to `".../{file=**}"`.
- No database/schema changes (the table remains `winche_files`).

## Self-review notes

- **Spec coverage:** table names = no-op (already `winche_files`, no column renames) ✓; hook registration → `UseHooks`/`HookRegistration`/`HookBuilder` (Task 2) ✓; Firestore path patterns via `PathMatcher` + `Winche.Rules` 2.1.0 (Tasks 1–2) ✓; sample + README + version (Tasks 2–3) ✓.
- **Unchanged on purpose:** `IHookInvocationDispatcher` (Readers stays `(FileStoreHook, Reader)`), `HookInvocationProcessor`, `FileStorage` Enqueue callers, the `AddSingleton<HookInvocationDispatcher>()` DI line (auto-resolves `IEnumerable<HookRegistration>`).
- **No test project exists** in WincheStorage; verification is `dotnet build`. The matching grammar itself is covered by Winche.Rules' own `PathMatcher` tests. If you want regression coverage for the dispatcher, a small test project could be added as a follow-up (out of scope here).
- **Type consistency:** `HookRegistration(string Path, FileStoreHook Hook)`, `UseHooks(Action<HookBuilder>)`, `HookBuilder.Add<THook>(string)` / `Add(string, FileStoreHook)`, `PathMatcher.IsMatch(pattern, path)` used consistently.
