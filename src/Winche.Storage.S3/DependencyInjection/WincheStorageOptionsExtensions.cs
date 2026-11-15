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
        return options.AllowS3Locations();
    }

    /// <summary>
    /// Lets the console move files to an S3 or S3-compatible bucket (AWS, Cloudflare R2, MinIO, Backblaze B2, …) with
    /// <c>o.UseSettings()</c>, even when the app keeps them elsewhere in code. <see cref="UseS3Archive"/> does this too.
    /// </summary>
    public static WincheStorageOptions AllowS3Locations(this WincheStorageOptions options)
    {
        if (!options.Services.Any(d => d.ImplementationType == typeof(S3LocationProvider)))
            options.Services.AddSingleton<Winche.Storage.Locations.IArchiveProvider, S3LocationProvider>();
        return options;
    }

    /// <summary>An S3 client for these options (shared by code-configured and console-chosen buckets).</summary>
    internal static IAmazonS3 Client(S3ArchiveOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.BucketName)) throw new InvalidOperationException("S3Archive: BucketName is required.");
        if (string.IsNullOrWhiteSpace(opts.RegionName)) throw new InvalidOperationException("S3Archive: RegionName is required.");
        if (!string.IsNullOrWhiteSpace(opts.AccessKey) && string.IsNullOrWhiteSpace(opts.SecretKey))
            throw new InvalidOperationException("S3Archive: AccessKey is set but SecretKey is missing.");
        if (string.IsNullOrWhiteSpace(opts.AccessKey) && !string.IsNullOrWhiteSpace(opts.SecretKey))
            throw new InvalidOperationException("S3Archive: SecretKey is set but AccessKey is missing.");
        if (!string.IsNullOrWhiteSpace(opts.ServiceUrl))
        {
            var config = new AmazonS3Config { ServiceURL = opts.ServiceUrl, ForcePathStyle = opts.ForcePathStyle, AuthenticationRegion = opts.RegionName };
            return string.IsNullOrWhiteSpace(opts.AccessKey) ? new AmazonS3Client(config) : new AmazonS3Client(opts.AccessKey, opts.SecretKey, config);
        }
        var region = RegionEndpoint.GetBySystemName(opts.RegionName);
        return string.IsNullOrWhiteSpace(opts.AccessKey) ? new AmazonS3Client(region) : new AmazonS3Client(opts.AccessKey, opts.SecretKey, region);
    }

    private static void RegisterS3ArchiveServices(IServiceCollection services)
    {
        services.AddSingleton<IAmazonS3>(sp => Client(sp.GetRequiredService<IOptions<S3ArchiveOptions>>().Value));

        // Registered inside the AddWincheStorage lambda → overrides the NullArchive default.
        services.AddSingleton<IArchive, S3Archive>();
    }
}

/// <summary>Builds S3 archives for buckets chosen in the console.</summary>
internal sealed class S3LocationProvider : Winche.Storage.Locations.IArchiveProvider
{
    public string Kind => "s3";

    public Winche.Storage.Interfaces.IArchive Create(Winche.Storage.Locations.ArchiveLocation location, string? secretKey)
    {
        var s = location.S3 ?? throw new InvalidOperationException("The location has no bucket.");
        var options = new S3ArchiveOptions
        {
            BucketName = s.Bucket, RegionName = s.Region, ServiceUrl = string.IsNullOrWhiteSpace(s.ServiceUrl) ? null : s.ServiceUrl,
            AccessKey = string.IsNullOrWhiteSpace(s.AccessKey) ? null : s.AccessKey, SecretKey = secretKey, ForcePathStyle = s.ForcePathStyle,
        };
        return new Archives.S3Archive(WincheStorageOptionsExtensions.Client(options), Microsoft.Extensions.Options.Options.Create(options));
    }
}
