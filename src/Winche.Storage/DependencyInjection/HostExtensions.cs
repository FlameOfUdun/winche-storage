using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Winche.Storage.Interfaces;

namespace Winche.Storage.DependencyInjection;

public static class HostExtensions
{
    /// <summary>Creates the storage table in the connection's search_path schema (idempotent).</summary>
    public static async Task InitializeWincheStorageAsync(this IHost host, CancellationToken ct = default)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISchemaManager>().EnsureCreatedAsync(ct);
    }
}
