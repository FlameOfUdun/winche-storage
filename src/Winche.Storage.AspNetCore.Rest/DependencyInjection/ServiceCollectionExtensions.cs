using Microsoft.Extensions.DependencyInjection;

namespace Winche.Storage.AspNetCore.Rest.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheStorageRestApi(this IServiceCollection services)
    {
        return services;
    }
}
