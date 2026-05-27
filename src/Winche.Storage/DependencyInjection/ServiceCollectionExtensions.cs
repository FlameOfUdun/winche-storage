using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Winche.Sentinel.DependencyInjection;
using Winche.Storage.Archives;
using Winche.Storage.Constants;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;
using Winche.Storage.Services;

namespace Winche.Storage.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DependencyConfigurator>? configure = null)
    {
        var connectionString =
            configuration.GetConnectionString("WincheStorage") ??
            configuration.GetConnectionString("DefaultConnection") ??
            throw new InvalidOperationException(
                "Connection string 'WincheStorage' or fallback 'DefaultConnection' is not configured.");

        return services.AddWincheStorage(
            options => configuration
                .GetSection(ServiceKeys.CONFIG_SECTION_KEY)
                .Bind(options),
            connectionString,
            configure);
    }

    public static IServiceCollection AddWincheStorage(
        this IServiceCollection services,
        Action<StoreOptions> configureOptions,
        string connectionString,
        Action<DependencyConfigurator>? configure = null)
    {
        services.Configure(configureOptions);

        return services.AddWincheStorageCore(connectionString, configure);
    }

    private static IServiceCollection AddWincheStorageCore(
        this IServiceCollection services,
        string connectionString,
        Action<DependencyConfigurator>? configure)
    {
        services.AddWincheSentinel<FileRecord>();

        services.AddNpgsqlDataSource(
            connectionString,
            serviceKey: ServiceKeys.DATA_SOURCE_KEY);

        services.AddSingleton<FileRecordAccessor>();
        services.AddSingleton<HookInvocationDispatcher>();
        services.AddHostedService<HookInvocationProcessor>();
        services.AddSingleton<IArchive, NullArchive>();
        services.AddSingleton<IFileManager, FileManager>();
        services.AddSingleton<ISchemaManager, SchemaManager>();

        configure?.Invoke(new DependencyConfigurator(services));

        return services;
    }
}