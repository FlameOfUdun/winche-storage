using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Rules;
using Winche.Rules.DependencyInjection;
using Winche.Storage.Archives;
using Winche.Storage.Authorization;
using Winche.Storage.Constants;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;
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

        services.AddNpgsqlDataSource(connectionString, serviceKey: WincheStorageKeys.DATA_SOURCE_KEY);

        // Schema + hooks
        services.AddSingleton<ISchemaManager, SchemaManager>();
        services.AddSingleton<HookInvocationDispatcher>();
        services.AddHostedService<HookInvocationProcessor>();
        services.AddSingleton(options.HookDelivery);
        services.AddHostedService<HookDeliveryService>();
        services.AddSingleton<StorageHookDeadLetters>();

        // This package owns an isolated rules engine, registered under a package-specific key so the
        // Storage engine and the Database engine never merge. Built from this package's own UseRules
        // rulesets (empty => deny-all). The engine is hot-swappable via a mutable rule-set repository,
        // still isolated under the package-specific key. Uses the engine's default DefaultRuleValueComparer
        // — the database's ValueComparer-backed comparer is a Winche.Database concern and not appropriate here.
        services.AddWincheRules(WincheStorageKeys.RULE_ENGINE_KEY, o => o
            .WithMutableRuleSetRepository(RuleSet.Merge(options.Rulesets)));

        // Guarded-by-default: IFileStorage resolves to the rules guard. The concrete FileStorage is the
        // unguarded core — inject it directly for trusted server-side callers that have no caller claims.
        var limits = new StorageLimits(options.MaxFileSizeBytes);
        services.AddSingleton(limits);
        if (options.OrphanSweepInCode || options.SettingsEnabled) services.AddHostedService<Services.OrphanSweepService>();
        if (options.SettingsEnabled)
        {
            // The console may change these: the code's values are where they start. The sweep's options are read
            // from DI once it's built; until then the code's sweep settings stand.
            var sweep = new OrphanSweepOptions { Enabled = options.OrphanSweepInCode };
            foreach (var d in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<OrphanSweepOptions>)).ToList())
                (d.ImplementationInstance as Microsoft.Extensions.Options.IConfigureOptions<OrphanSweepOptions>)?.Configure(sweep);
            sweep.Enabled = options.OrphanSweepInCode;
            services.AddSingleton<Microsoft.Extensions.Options.IOptions<OrphanSweepOptions>>(Microsoft.Extensions.Options.Options.Create(sweep));
            var area = Winche.Config.ServiceCollectionExtensions.AddWincheSettingsArea(services, Settings.StorageSettings.Area, "Storage",
                Settings.StorageSettings.From(options, sweep, options.OrphanSweepInCode), s => s.Problems());
            area.Changed += s => s.ApplyTo(options, limits, sweep);
        }
        if (options.SettingsEnabled)
        {
            // Where files are kept can change from the console (a guided move): FileStorage goes through the router,
            // and the archive registered in code (the last IArchive) is "the app's own location".
            var locationArea = Winche.Config.ServiceCollectionExtensions.AddWincheSettingsArea(services, Locations.StorageLocationSettings.Area,
                "File location", new Locations.StorageLocationSettings(), s => s.Problems());
            services.AddSingleton(sp =>
            {
                IArchive? code = null;
                return new Locations.ArchiveLocations(sp, () => code ??= sp.GetServices<IArchive>().Last());
            });
            services.AddSingleton(sp => new Locations.ArchiveRouter(locationArea, sp.GetRequiredService<Locations.ArchiveLocations>()));
            services.AddSingleton<Locations.StorageMoves>();
            services.AddHostedService<Locations.StorageMoveWorker>();
            services.AddSingleton(sp => new FileStorage(
                sp.GetRequiredKeyedService<Npgsql.NpgsqlDataSource>(WincheStorageKeys.DATA_SOURCE_KEY),
                sp.GetRequiredService<Locations.ArchiveRouter>(), sp.GetRequiredService<HookInvocationDispatcher>(), limits));
        }
        else
            services.AddSingleton<FileStorage>();
        services.AddSingleton<RuleGuardedFileStorage>(sp =>
            new RuleGuardedFileStorage(
                sp.GetRequiredService<FileStorage>(),
                sp.GetRequiredKeyedService<RuleEngine>(WincheStorageKeys.RULE_ENGINE_KEY),
                () => sp.GetRequiredService<IRuleClaimsAccessor>().GetClaims(),
                options.RequestLog is not null || options.SettingsEnabled
                    ? new Logging.RequestLog(
                        (sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
                            .CreateLogger(Logging.RequestLog.Category),
                        () => options.RequestLog, () => sp.GetRequiredService<IRuleClaimsAccessor>().GetClaims())
                    : null));
        services.AddSingleton<IFileStorage>(sp =>
            sp.GetRequiredService<RuleGuardedFileStorage>());

        return services;
    }
}
