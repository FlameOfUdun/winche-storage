using System;
using Winche.Storage.Infrastructure;
using Xunit;

namespace Winche.Storage.Tests;

public class DirectoryPageTokenTests
{
    [Fact]
    public void RoundTrip_Encode_then_Decode_returns_original()
    {
        const string directoryId = "images/2024/thumbnails";
        var token = DirectoryPageToken.Encode(directoryId);
        var decoded = DirectoryPageToken.Decode(token);
        Assert.Equal(directoryId, decoded);
    }

    [Fact]
    public void Decode_empty_string_throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => DirectoryPageToken.Decode(""));
    }

    [Fact]
    public void Decode_invalid_base64_throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => DirectoryPageToken.Decode("not base64!!!"));
    }
}
