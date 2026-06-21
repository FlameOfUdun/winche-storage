using System.Reflection;
using Winche.Storage.Interfaces;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.Tests;

public class ListDirectoryIdsSurfaceTests
{
    [Fact]
    public void FileStorage_exposes_ListDirectoryIdsAsync()
    {
        var method = typeof(FileStorage).GetMethod("ListDirectoryIdsAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IFileStorage_does_NOT_expose_ListDirectoryIdsAsync()
    {
        var method = typeof(IFileStorage).GetMethod("ListDirectoryIdsAsync");
        Assert.Null(method);
    }
}
