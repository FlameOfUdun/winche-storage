using Microsoft.Extensions.DependencyInjection;
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
        // Defaults registered BEFORE configure so user overrides (MapClaims/UseS3Archive) win
        // (.NET DI returns the last-registered singleton).
        services.AddSingleton<IRuleClaimsAccessor>(NullRuleClaimsAccessor.Instance);
        services.AddSingleton<IArchive, NullArchive>();

        var options = new WincheStorageOptions(services);
        configure(options);

        var connectionString = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? options.ConnectionString
            : throw new InvalidOperationException(
                $"{nameof(WincheStorageOptions)}.{nameof(WincheStorageOptions.ConnectionString)} is required.");

        services.AddNpgsqlDataSource(connectionString, serviceKey: ServiceKeys.DATA_SOURCE_KEY);

        // Schema + hooks
        services.AddSingleton<ISchemaManager, SchemaManager>();
        services.AddSingleton<HookInvocationDispatcher>();
        services.AddHostedService<HookInvocationProcessor>();

        // Winche.Rules guard: merges every RuleSet from UseRules() plus this deny-all seed.
        services.AddWincheRules(o => o.WithRuleset(_ => { }));

        // Guarded-by-default: IFileStorage resolves to the rules guard. The concrete FileStorage is the
        // unguarded core — inject it directly for trusted server-side callers that have no caller claims.
        services.AddSingleton<FileStorage>();
        services.AddSingleton<RuleGuardedFileStorage>(sp =>
            new RuleGuardedFileStorage(
                sp.GetRequiredService<FileStorage>(),
                sp.GetRequiredService<RuleEngine>(),
                () => sp.GetRequiredService<IRuleClaimsAccessor>().GetClaims()));
        services.AddSingleton<IFileStorage>(sp =>
            sp.GetRequiredService<RuleGuardedFileStorage>());

        return services;
    }
}
