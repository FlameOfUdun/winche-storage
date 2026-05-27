using Microsoft.Extensions.DependencyInjection;
using Winche.Sentinel.DependencyInjection;
using Winche.Storage.Abstraction;
using Winche.Storage.Models;

namespace Winche.Storage.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public IServiceCollection Services => services;

    public DependencyConfigurator AddFileAccessRule<TRule>() where TRule : FileAccessRule
    {
        services.ConfigureWincheSentinel<FileRecord>(configurator =>
        {
            configurator.AddResourceAccessRule<TRule>();
        });

        return this;
    }

    public DependencyConfigurator AddFileStoreHook<THook>() where THook : FileStoreHook
    {
        services.AddSingleton<FileStoreHook, THook>();

        return this;
    }

    public DependencyConfigurator ConfigureServices(Action<IServiceCollection> configure)
    {
        configure(services);
        return this;
    }
}
