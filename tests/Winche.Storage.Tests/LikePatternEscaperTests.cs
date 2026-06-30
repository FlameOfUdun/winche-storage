using Winche.Storage.Infrastructure;
using Xunit;

namespace Winche.Storage.Tests;

public class LikePatternEscaperTests
{
    [Theory]
    [InlineData("plain/path", "plain/path")]      // nothing to escape
    [InlineData("50%", @"50\%")]                   // wildcard
    [InlineData("a_b", @"a\_b")]                   // single-char wildcard
    [InlineData(@"a\b", @"a\\b")]                  // the escape char itself
    [InlineData("%_\\", @"\%\_\\")]                // all three, combined
    public void Escapes_like_metacharacters(string input, string expected)
    {
        Assert.Equal(expected, LikePatternEscaper.Escape(input));
    }

    [Fact]
    public void Empty_input_is_returned_unchanged()
    {
        Assert.Equal("", LikePatternEscaper.Escape(""));
    }

    [Fact]
    public void Escaped_prefix_only_matches_literally_under_LIKE()
    {
        // A path containing '%' must not become a wildcard when used as a subtree prefix.
        var escaped = LikePatternEscaper.Escape("a%b");
        Assert.Equal(@"a\%b", escaped);
        Assert.DoesNotContain("%b", escaped[..^2]); // the '%' is preceded by a backslash
    }
}
