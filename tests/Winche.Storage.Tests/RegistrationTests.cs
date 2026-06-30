using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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

    [Fact]
    public void UseOrphanSweep_registers_the_background_sweep_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddWincheStorage(o =>
        {
            o.ConnectionString = "Host=localhost;Port=5432;Database=test;Username=test;Password=test";
            o.UseOrphanSweep();
        });

        var registered = services.Any(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType?.Name == "OrphanSweepService");

        Assert.True(registered);
    }

    [Fact]
    public void UseOrphanSweep_defaults_to_a_24h_grace_window()
    {
        var services = new ServiceCollection();
        services.AddWincheStorage(o =>
        {
            o.ConnectionString = "Host=localhost;Port=5432;Database=test;Username=test;Password=test";
            o.UseOrphanSweep();
        });

        using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<OrphanSweepOptions>>().Value;

        Assert.Equal(TimeSpan.FromHours(24), opts.GraceWindow);
    }

    [Fact]
    public void Orphan_sweep_is_not_registered_unless_opted_in()
    {
        var services = new ServiceCollection();
        services.AddWincheStorage(o =>
        {
            o.ConnectionString = "Host=localhost;Port=5432;Database=test;Username=test;Password=test";
        });

        var registered = services.Any(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType?.Name == "OrphanSweepService");

        Assert.False(registered);
    }
}
