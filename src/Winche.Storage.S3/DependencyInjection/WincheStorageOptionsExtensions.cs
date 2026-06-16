using Amazon;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;
using Winche.Storage.S3.Archives;
using Winche.Storage.S3.Models;

namespace Winche.Storage.S3.DependencyInjection;

public static class WincheStorageOptionsExtensions
{
    public static WincheStorageOptions UseS3Archive(this WincheStorageOptions options, Action<S3ArchiveOptions> configure)
    {
        options.Services.Configure(configure);
        RegisterS3ArchiveServices(options.Services);
        return options;
    }

    private static void RegisterS3ArchiveServices(IServiceCollection services)
    {
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<S3ArchiveOptions>>().Value;

            if (string.IsNullOrWhiteSpace(opts.BucketName))
                throw new InvalidOperationException("S3Archive: BucketName is required.");

            if (string.IsNullOrWhiteSpace(opts.RegionName))
                throw new InvalidOperationException("S3Archive: RegionName is required.");

            if (!string.IsNullOrWhiteSpace(opts.AccessKey) && string.IsNullOrWhiteSpace(opts.SecretKey))
                throw new InvalidOperationException("S3Archive: AccessKey is set but SecretKey is missing.");

            if (string.IsNullOrWhiteSpace(opts.AccessKey) && !string.IsNullOrWhiteSpace(opts.SecretKey))
                throw new InvalidOperationException("S3Archive: SecretKey is set but AccessKey is missing.");

            var region = RegionEndpoint.GetBySystemName(opts.RegionName);

            return string.IsNullOrWhiteSpace(opts.AccessKey)
                ? new AmazonS3Client(region)
                : new AmazonS3Client(opts.AccessKey, opts.SecretKey, region);
        });

        // Registered inside the AddWincheStorage lambda → overrides the NullArchive default.
        services.AddSingleton<IArchive, S3Archive>();
    }
}
