using Amazon;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Winche.Storage.DependencyInjection;
using Winche.Storage.S3.DependencyInjection;
using Xunit;

namespace Winche.Storage.Tests;

public class S3ArchiveConfigTests
{
    private const string Conn = "Host=localhost;Port=5432;Database=test;Username=test;Password=test";

    private static IAmazonS3 ResolveClient(Action<Winche.Storage.S3.Models.S3ArchiveOptions> configureS3)
    {
        var services = new ServiceCollection();
        services.AddWincheStorage(o =>
        {
            o.ConnectionString = Conn;
            o.UseS3Archive(configureS3);
        });
        return services.BuildServiceProvider().GetRequiredService<IAmazonS3>();
    }

    [Fact]
    public void Custom_ServiceUrl_is_applied_with_path_style()
    {
        var client = ResolveClient(s3 =>
        {
            s3.BucketName = "bucket";
            s3.RegionName = "us-east-1";
            s3.AccessKey = "minio";
            s3.SecretKey = "minio123";
            s3.ServiceUrl = "http://localhost:9000";
            s3.ForcePathStyle = true;
        });

        // The AWS SDK canonicalizes ServiceURL with a trailing slash.
        Assert.StartsWith("http://localhost:9000", client.Config.ServiceURL);
        Assert.True(((AmazonS3Config)client.Config).ForcePathStyle);
    }

    [Fact]
    public void ForcePathStyle_defaults_to_true_when_ServiceUrl_is_set()
    {
        var client = ResolveClient(s3 =>
        {
            s3.BucketName = "bucket";
            s3.RegionName = "us-east-1";
            s3.ServiceUrl = "http://localhost:9000";
            // ForcePathStyle intentionally not set
        });

        Assert.True(((AmazonS3Config)client.Config).ForcePathStyle);
    }

    [Fact]
    public void Without_ServiceUrl_the_region_endpoint_is_used()
    {
        var client = ResolveClient(s3 =>
        {
            s3.BucketName = "bucket";
            s3.RegionName = "us-east-1";
            // no ServiceUrl => real AWS behavior
        });

        Assert.True(string.IsNullOrEmpty(client.Config.ServiceURL));
        Assert.Equal(RegionEndpoint.GetBySystemName("us-east-1"), client.Config.RegionEndpoint);
    }
}
