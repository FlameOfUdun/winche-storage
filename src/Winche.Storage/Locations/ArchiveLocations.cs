using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Winche.Config;
using Winche.Storage.Interfaces;

namespace Winche.Storage.Locations;

/// <summary>Builds (and keeps) the archive for each location: the code's one, or one made by a provider from settings.</summary>
public sealed class ArchiveLocations(IServiceProvider services, Func<IArchive> code)
{
    private readonly ConcurrentDictionary<string, (IArchive Archive, DateTimeOffset? SecretSetAt)> _built = new();

    public IReadOnlyList<string> Kinds => [.. services.GetServices<IArchiveProvider>().Select(p => p.Kind)];

    /// <summary>The app's own archive (from code).</summary>
    public IArchive Code => code();

    public async Task<IArchive> GetAsync(ArchiveLocation? location, CancellationToken ct = default)
    {
        if (location is null || location.Kind == "code") return Code;
        var secrets = services.GetService<SecretStore>();
        var info = secrets is null ? null : await secrets.InfoAsync(location.SecretName, ct);
        var key = $"{location.Id}|{System.Text.Json.JsonSerializer.Serialize(location)}";
        if (_built.TryGetValue(key, out var hit) && hit.SecretSetAt == info?.SetAt) return hit.Archive;
        var secret = secrets is null || info is null ? null : await secrets.GetAsync(location.SecretName, ct);
        var archive = Build(location, secret);
        _built[key] = (archive, info?.SetAt);
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
