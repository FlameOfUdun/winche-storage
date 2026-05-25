using Amazon;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WincheSentinel.DependencyInjection;
using Winche.Storage.Archives;
using Winche.Storage.Models;
using Winche.Storage.Services;
using Winche.Storage.Interfaces;
using Winche.Storage.Constants;

namespace Winche.Storage.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheStorage(this IServiceCollection services, string connectionString, IConfiguration configuration, Action<DependencyConfigurator>? configure = null)
    {
        services.Configure<StoreOptions>(configuration.GetSection(ServiceKeys.CONFIG_SECTION_KEY));

        var contextAccessor = new CallerContextAccessor();

        services.AddWincheSentinel<FileRecord>(c =>
        {
            c.AddResourceObjectAccessor<FileRecordAccessor>();
            c.AddCallerContextAccessor(contextAccessor);
        });
            

        configure?.Invoke(new DependencyConfigurator(services));

        services.AddNpgsqlDataSource(connectionString, serviceKey: ServiceKeys.DATA_SOURCE_KEY);

        services.AddSingleton(contextAccessor);
        services.AddSingleton<HookInvocationDispatcher>();
        services.AddHostedService<HookInvocationProcessor>();
        services.AddSingleton<IFileManager, FileManager>();
        services.AddSingleton<ISchemaManager, SchemaManager>();

        var s3Options = configuration.GetSection(ServiceKeys.S3_ARCHIVE_SECTION_KEY).Get<S3ArchiveOptions>();
        if (s3Options is not null)
        {
            services.AddSingleton<IAmazonS3>(_ =>
            {
                var region = RegionEndpoint.GetBySystemName(s3Options.RegionName);
                return new AmazonS3Client(s3Options.AccessKey, s3Options.SecretKey, region);
            });
            services.AddSingleton<IArchive, S3Archive>();
        }

        return services;
    }
}