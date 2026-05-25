namespace Winche.Storage.Models
{
    public sealed record StoreOptions
    {
        public string Schema { get; set; } = "public";
        public string TableName { get; set; } = "files";
        public bool EnsureCreated { get; set; } = true;
        public S3ArchiveOptions? S3Archive { get; set; }
    }

    public sealed record S3ArchiveOptions
    {
        public string BucketName { get; init; } = string.Empty;
        public string AccessKey { get; init; } = string.Empty;
        public string SecretKey { get; init; } = string.Empty;
        public string RegionName { get; init; } = string.Empty;
        public TimeSpan PresignedUrlExpiry { get; init; } = TimeSpan.FromMinutes(15);
    }
}
