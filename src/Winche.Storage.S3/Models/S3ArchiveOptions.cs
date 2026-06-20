namespace Winche.Storage.S3.Models;

/// <summary>
/// Options for configuring S3 archive storage.
/// </summary>
public sealed record S3ArchiveOptions
{
    /// <summary>
    /// The name of the S3 bucket to use for storing archived files.
    /// </summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// The access key for authenticating with the S3 service. This is optional if the application is running in an environment that provides credentials through other means (e.g., IAM roles, environment variables).
    /// </summary>
    public string? AccessKey { get; set; }

    /// <summary>
    /// The secret key for authenticating with the S3 service. This is optional if the application is running in an environment that provides credentials through other means (e.g., IAM roles, environment variables).
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// The name of the AWS region where the S3 bucket is located.
    /// </summary>
    public string RegionName { get; set; } = string.Empty;

    /// <summary>
    /// Custom S3-compatible endpoint (e.g. "http://localhost:9000" for MinIO). Leave null/empty to
    /// target real AWS S3 (the default). When set, presigned URLs are generated against this endpoint.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// Use path-style addressing (bucket in the path, not the host). Required by MinIO and most
    /// S3-compatible stores. Only consulted when <see cref="ServiceUrl"/> is set; defaults to true.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// The expiry time for presigned URLs generated for accessing S3 objects.
    /// </summary>
    public TimeSpan PresignedUrlExpiry { get; set; } = TimeSpan.FromMinutes(15);
}
