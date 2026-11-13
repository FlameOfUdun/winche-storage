using System.Threading.Tasks;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;
using Xunit;

namespace Winche.Storage.Tests;

public class ContentHashSurfaceTests
{
    [Fact]
    public void FileRecord_exposes_nullable_ContentHash_string()
    {
        var prop = typeof(FileRecord).GetProperty("ContentHash");
        Assert.NotNull(prop);
        Assert.Equal(typeof(string), prop!.PropertyType);
    }

    [Fact]
    public void IArchive_exposes_HeadAsync_with_size_and_etag()
    {
        var m = typeof(IArchive).GetMethod("HeadAsync");
        Assert.NotNull(m);
        Assert.Equal(typeof(Task<ArchiveObjectInfo?>), m!.ReturnType);
    }
}
