using Winche.Storage.AspNetCore.Rest.Infrastructure;
using Xunit;

namespace Winche.Storage.Tests;

public class Base64UrlPathTests
{
    [Theory]
    [InlineData("a/b/c.txt")]
    [InlineData("file.txt")]
    [InlineData("dir/with spaces/and-+slash_/x")]
    [InlineData("ünïcode/π.bin")]
    [InlineData("a")]                 // 1 byte  => base64 len 4 with == padding
    [InlineData("ab")]                // 2 bytes => one '=' of padding
    [InlineData("abc")]               // 3 bytes => no padding
    public void Decode_is_inverse_of_Encode(string path)
    {
        Assert.Equal(path, Base64UrlPath.Decode(Base64UrlPath.Encode(path)));
    }

    [Fact]
    public void Encode_uses_url_safe_alphabet_without_padding()
    {
        // "~~~ÿ" forces bytes that base64-encode with '+' and '/' in the standard alphabet.
        var encoded = Base64UrlPath.Encode("ÿÿÿ");
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Fact]
    public void Decode_accepts_the_url_safe_alphabet()
    {
        // '_' is base64url for '/', '-' for '+'. Decoding must map them back.
        var roundTrip = Base64UrlPath.Decode(Base64UrlPath.Encode("ÿÿÿ"));
        Assert.Equal("ÿÿÿ", roundTrip);
    }
}
