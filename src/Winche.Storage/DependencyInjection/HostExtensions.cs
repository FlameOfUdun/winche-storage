using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Winche.Storage.Interfaces;

namespace Winche.Storage.DependencyInjection;

public static class HostExtensions
{
    public static IHost UseWincheStorage(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetService<ISchemaManager>()!;
        service.EnsureCreatedAsync().GetAwaiter().GetResult();

        return host;
    }
}
