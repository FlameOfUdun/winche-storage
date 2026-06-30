using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Winche.Storage.S3.Archives;
using Winche.Storage.S3.Models;
using Xunit;

namespace Winche.Storage.Tests;

public class S3ArchiveDeleteTests
{
    private static S3Archive Archive(IAmazonS3 s3) =>
        new(s3, Options.Create(new S3ArchiveOptions { BucketName = "bucket" }));

    [Fact]
    public async Task DeleteObjectsAsync_does_not_throw_when_S3_reports_per_key_errors()
    {
        var s3 = new FakeAmazonS3
        {
            OnDeleteObjects = _ => new DeleteObjectsResponse
            {
                DeleteErrors = [new DeleteError { Key = "k", Code = "AccessDenied", Message = "no" }],
            },
        };

        var ex = await Record.ExceptionAsync(() => Archive(s3).DeleteObjectsAsync(["k"]));

        Assert.Null(ex);
    }

    [Fact]
    public async Task DeleteObjectsAsync_swallows_transport_exceptions()
    {
        var s3 = new FakeAmazonS3 { OnDeleteObjects = _ => throw new AmazonS3Exception("boom") };

        var ex = await Record.ExceptionAsync(() => Archive(s3).DeleteObjectsAsync(["k"]));

        Assert.Null(ex);
    }

    [Fact]
    public async Task ListObjectsAsync_yields_objects_across_pages()
    {
        var s3 = new FakeAmazonS3
        {
            OnListObjectsV2 = req => req.ContinuationToken is null
                ? new ListObjectsV2Response
                {
                    S3Objects = [new S3Object { Key = "a", LastModified = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }],
                    IsTruncated = true,
                    NextContinuationToken = "tok",
                }
                : new ListObjectsV2Response
                {
                    S3Objects = [new S3Object { Key = "b", LastModified = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc) }],
                    IsTruncated = false,
                },
        };

        var keys = new List<string>();
        await foreach (var o in Archive(s3).ListObjectsAsync(null))
            keys.Add(o.Key);

        Assert.Equal(new[] { "a", "b" }, keys);
    }
}
