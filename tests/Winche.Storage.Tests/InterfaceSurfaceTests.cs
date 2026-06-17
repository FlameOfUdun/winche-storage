using System.Linq;
using Winche.Storage.Interfaces;
using Xunit;

namespace Winche.Storage.Tests;

public class InterfaceSurfaceTests
{
    [Fact]
    public void IFileStorage_exposes_no_Unprotected_methods()
    {
        var leaked = typeof(IFileStorage)
            .GetMethods()
            .Select(m => m.Name)
            .Where(n => n.Contains("Unprotected"))
            .ToArray();

        Assert.True(
            leaked.Length == 0,
            $"IFileStorage must not expose Unprotected methods, found: {string.Join(", ", leaked)}");
    }
}
