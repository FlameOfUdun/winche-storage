using Microsoft.Extensions.DependencyInjection;
using Winche.Storage.Authorization;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.Tests;

public class RegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddWincheStorage(o =>
        {
            // A well-formed connection string is enough; nothing connects during resolution.
            o.ConnectionString = "Host=localhost;Port=5432;Database=test;Username=test;Password=test";
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void IFileStorage_resolves_to_the_rules_guard()
    {
        using var provider = BuildProvider();
        var resolved = provider.GetRequiredService<IFileStorage>();
        Assert.IsType<RuleGuardedFileStorage>(resolved);
    }

    [Fact]
    public void Concrete_FileStorage_resolves_to_the_unguarded_core()
    {
        using var provider = BuildProvider();
        var resolved = provider.GetRequiredService<FileStorage>();
        Assert.IsType<FileStorage>(resolved);
        Assert.IsNotType<RuleGuardedFileStorage>(resolved);
    }
}
