using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.Coordination;
using Winche.Storage.Interfaces;

namespace Winche.Storage.DependencyInjection;

public static class HostExtensions
{
    /// <summary>Creates the storage table in the connection's search_path schema (idempotent).</summary>
    /// <summary>
    /// Creates the storage tables (idempotent). Nodes starting together take turns (a Postgres advisory
    /// lock), so concurrent DDL on the same objects can't fail or deadlock.
    /// </summary>
    public static async Task InitializeWincheStorageAsync(this IHost host, CancellationToken ct = default)
    {
        using var scope = host.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredKeyedService<NpgsqlDataSource>(WincheStorageKeys.DATA_SOURCE_KEY);
        await using var _ = await AdvisoryLease.AcquireAsync(source, "storage-schema", TimeSpan.FromSeconds(30), ct);
        await scope.ServiceProvider.GetRequiredService<ISchemaManager>().EnsureCreatedAsync(ct);
    }
}
