using Microsoft.Extensions.DependencyInjection;

namespace Winche.Storage.AspNetCore.Rest.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheStorageRestApi(this IServiceCollection services, Action<DependencyConfigurator>? configure = null)
    {
        var configurator = new DependencyConfigurator(services);
        configure?.Invoke(configurator);

        return services;
    }
}