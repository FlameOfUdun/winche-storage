using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Winche.Core;
using Winche.Secrets;
using Winche.Storage.Interfaces;

namespace Winche.Storage.Locations;

/// <summary>Builds (and keeps) the archive for each location: the code's one, or one made by a provider from settings.</summary>
public sealed class ArchiveLocations(IServiceProvider services, Func<IArchive> code)
{
    private readonly ConcurrentDictionary<string, (IArchive Archive, string? SecretStamp)> _built = new();

    public IReadOnlyList<string> Kinds => [.. services.GetServices<IArchiveProvider>().Select(p => p.Kind)];

    /// <summary>The app's own archive (from code).</summary>
    public IArchive Code => code();

    public async Task<IArchive> GetAsync(ArchiveLocation? location, CancellationToken ct = default)
    {
        if (location is null || location.Kind == "code") return Code;
        var secrets = services.GetService<SecretStore>();
        var key = $"{location.Id}|{System.Text.Json.JsonSerializer.Serialize(location)}";
        var cached = _built.TryGetValue(key, out var hit);
        // The key's value, not when it was set: one read from elsewhere (AWS) may change there (a rotation).
        string? secret;
        try { secret = secrets is null ? null : await secrets.GetAsync(location.SecretName, ct); }
        catch (SecretReferenceException) when (cached) { return hit.Archive; }   // can't be read just now: keep using the one that works
        var stamp = secret is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));
        if (cached && hit.SecretStamp == stamp) return hit.Archive;
        var archive = Build(location, secret);
        _built[key] = (archive, stamp);
        return archive;
    }

    /// <summary>An archive for a location not saved yet (testing it), with its secret given directly.</summary>
    public IArchive Build(ArchiveLocation location, string? secretKey)
    {
        var provider = services.GetServices<IArchiveProvider>().FirstOrDefault(p => p.Kind == location.Kind)
            ?? throw new InvalidOperationException($"This app can't keep files in '{location.Kind}' locations: add the package that provides them.");
        return provider.Create(location, secretKey);
    }
}
