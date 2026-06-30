using Winche.Storage.Infrastructure;
using Xunit;

namespace Winche.Storage.Tests;

public class FilePathParserTests
{
    [Fact]
    public void Single_segment_has_empty_directory()
    {
        var info = FilePathParser.Parse("file.txt");
        Assert.Equal("", info.Directory);
        Assert.Equal("file.txt", info.Id);
    }

    [Fact]
    public void Multi_segment_splits_directory_and_id()
    {
        var info = FilePathParser.Parse("a/b/c.txt");
        Assert.Equal("a/b", info.Directory);
        Assert.Equal("c.txt", info.Id);
    }

    [Theory]
    [InlineData("/a/b")]   // leading slash
    [InlineData("a//b")]   // duplicate slash
    [InlineData("a/b/")]   // trailing slash
    public void Empty_segments_are_collapsed(string path)
    {
        var info = FilePathParser.Parse(path);
        Assert.Equal("a", info.Directory);
        Assert.Equal("b", info.Id);
    }

    [Fact]
    public void Parse_rejects_empty_path()
    {
        Assert.Throws<ArgumentException>(() => FilePathParser.Parse(""));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("a/b/c", true)]
    [InlineData("", false)]    // empty
    [InlineData("/", false)]   // only separators => zero segments
    public void IsValidPath_reports_segment_presence(string path, bool expected)
    {
        var ok = FilePathParser.IsValidPath(path, out var error);
        Assert.Equal(expected, ok);
        if (!expected) Assert.NotNull(error);
    }
}
