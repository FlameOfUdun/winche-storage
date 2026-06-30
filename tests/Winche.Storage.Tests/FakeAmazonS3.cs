using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace Winche.Storage.Tests;

/// <summary>
/// Minimal <see cref="AmazonS3Client"/> stand-in: overrides only the operations the archive tests
/// exercise. No network is touched — the base ctor just builds config from dummy credentials.
/// </summary>
internal sealed class FakeAmazonS3 : AmazonS3Client
{
    public FakeAmazonS3() : base("ak", "sk", RegionEndpoint.USEast1) { }

    public Func<DeleteObjectsRequest, DeleteObjectsResponse>? OnDeleteObjects { get; set; }
    public Func<ListObjectsV2Request, ListObjectsV2Response>? OnListObjectsV2 { get; set; }
    public int DeleteObjectsCallCount { get; private set; }

    public override Task<DeleteObjectsResponse> DeleteObjectsAsync(
        DeleteObjectsRequest request, CancellationToken cancellationToken = default)
    {
        DeleteObjectsCallCount++;
        var result = OnDeleteObjects?.Invoke(request) ?? new DeleteObjectsResponse();
        return Task.FromResult(result);
    }

    public override Task<ListObjectsV2Response> ListObjectsV2Async(
        ListObjectsV2Request request, CancellationToken cancellationToken = default)
    {
        var result = OnListObjectsV2?.Invoke(request) ?? new ListObjectsV2Response();
        return Task.FromResult(result);
    }
}
