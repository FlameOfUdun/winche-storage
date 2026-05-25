using Microsoft.Extensions.DependencyInjection;
using Winche.Storage.AspNetCore.Rest.Abstraction;

namespace Winche.Storage.AspNetCore.Rest.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public DependencyConfigurator AddClaimsMapper<TMapper>() where TMapper : RestClaimsMapper
    {
        services.AddSingleton<RestClaimsMapper, TMapper>();

        return this;
    }
}
