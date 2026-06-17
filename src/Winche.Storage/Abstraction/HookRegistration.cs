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
