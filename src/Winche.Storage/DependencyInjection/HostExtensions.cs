using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Winche.Storage.Interfaces;

namespace Winche.Storage.DependencyInjection;

public static class HostExtensions
{
    public static IHost UseWincheStorage(this IHost host)
    {
        Task.Run(async () =>
        {
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISchemaManager>().EnsureCreatedAsync();
        }).GetAwaiter().GetResult();

        return host;
    }
}
