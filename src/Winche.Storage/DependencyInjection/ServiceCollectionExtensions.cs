using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Rules;
using Winche.Rules.DependencyInjection;
using Winche.Storage.Archives;
using Winche.Storage.Authorization;
using Winche.Storage.Constants;
using Winche.Storage.Interfaces;
using Winche.Storage.Services;

namespace Winche.Storage.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheStorage(
        this IServiceCollection services,
        Action<WincheStorageOptions> configure)
    {
        // Register defaults BEFORE the lambda so later registrations win (last-registered singleton):
        //   - null claims fallback  → overridden by MapClaims()
        //   - NullArchive default   → overridden by AddS3Archive()
        services.AddSingleton<IRuleClaimsAccessor>(NullRuleClaimsAccessor.Instance);
        services.AddSingleton<IArchive, NullArchive>();

        var options = new WincheStorageOptions(services);
        configure(options);

        var connectionString = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? options.ConnectionString
            : throw new InvalidOperationException(
                $"{nameof(WincheStorageOptions)}.{nameof(WincheStorageOptions.ConnectionString)} is required.");

        services.AddSingleton<IOptions<WincheStorageOptions>>(Options.Create(options));

        services.AddNpgsqlDataSource(connectionString, serviceKey: ServiceKeys.DATA_SOURCE_KEY);

        services.AddSingleton<HookInvocationDispatcher>();
        services.AddHostedService<HookInvocationProcessor>();
        services.AddSingleton<ISchemaManager, SchemaManager>();

        // Winche.Rules engine: merges every RuleSet from UseRules() plus this deny-all seed.
        services.AddWincheRules(o => o.WithRuleset(_ => { }));

        // Unguarded core, then the authorize-then-delegate guard as the public IFileManager.
        services.AddSingleton<FileManager>();
        services.AddSingleton<RuleGuardedFileManager>(sp =>
            new RuleGuardedFileManager(
                sp.GetRequiredService<FileManager>(),
                sp.GetRequiredService<RuleEngine>(),
                () => sp.GetRequiredService<IRuleClaimsAccessor>().GetClaims()));
        services.AddSingleton<IFileManager>(sp =>
            sp.GetRequiredService<RuleGuardedFileManager>());

        return services;
    }
}
