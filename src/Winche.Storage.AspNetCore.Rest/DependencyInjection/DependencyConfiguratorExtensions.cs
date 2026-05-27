using Microsoft.Extensions.DependencyInjection;
using Winche.Sentinel.DependencyInjection;
using Winche.Sentinel.Interfaces;
using Winche.Storage.AspNetCore.Rest.Abstraction;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Models;

namespace Winche.Storage.AspNetCore.Rest.DependencyInjection;

public static class DependencyConfiguratorExtensions
{
    public static DependencyConfigurator SetCallerClaimsAccessor<TAccessor>(this DependencyConfigurator configurator)
        where TAccessor : FileClaimsAccessor
    {
        configurator.Services.ConfigureWincheSentinel<FileRecord>(c => c.SetCallerClaimsAccessor<TAccessor>());
        configurator.Services.AddSingleton(sp => (FileClaimsAccessor)sp.GetRequiredService<ICallerClaimsAccessor<FileRecord>>());
        return configurator;
    }
}
