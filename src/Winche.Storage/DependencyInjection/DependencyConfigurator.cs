using Microsoft.Extensions.DependencyInjection;
using Winche.Storage.Abstraction;
using Winche.Storage.Models;
using WincheSentinel.DependencyInjection;

namespace Winche.Storage.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
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
}
