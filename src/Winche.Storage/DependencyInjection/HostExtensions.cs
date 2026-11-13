using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Storage.Constants;
using Winche.Core;
using Winche.Storage.Interfaces;

namespace Winche.Storage.DependencyInjection;

public static class HostExtensions
{
    /// <summary>
    /// Creates the storage tables (idempotent). Nodes starting together take turns (a Postgres advisory
    /// lock), so concurrent DDL on the same objects can't fail or deadlock.
    /// </summary>
    public static async Task InitializeWincheStorageAsync(this IHost host, CancellationToken ct = default)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISchemaManager>().EnsureCreatedAsync(ct);
        // Registered hooks keep every change from now on, even before their delivery first runs.
        await scope.ServiceProvider.GetRequiredService<Services.StorageHooks>().TrackAsync(ct);
    }
}
