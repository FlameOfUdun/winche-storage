using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Winche.Rules;
using Winche.Storage.Abstraction;

namespace Winche.Storage.DependencyInjection;

/// <summary>
/// The single options surface for Winche.Storage: connection and component registrations
/// (rules, hooks). Configured through the <c>AddWincheStorage</c> lambda; its values are applied
/// at registration time — the connection string is read directly and the registrations it performs
/// go through <see cref="Services"/>. Satellite packages extend this type (e.g. <c>UseS3Archive</c>,
/// <c>MapClaims</c>) through <see cref="Services"/>.
/// </summary>
public sealed class WincheStorageOptions
{
    private readonly IServiceCollection? _services;

    public WincheStorageOptions() { }

    internal WincheStorageOptions(IServiceCollection services) => _services = services;

    /// <summary>Registration handle for extension packages; available only inside the AddWincheStorage lambda.</summary>
    public IServiceCollection Services => _services
        ?? throw new InvalidOperationException("Services is available only within the AddWincheStorage configuration lambda.");

    /// <summary>
    /// Required. Postgres connection string. All objects live in the connection's search_path
    /// schema — use <c>Search Path=myschema</c> here for non-<c>public</c> deployments
    /// (same convention as <c>Winche.Database</c>).
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Rulesets registered via <see cref="UseRules(RuleSet)"/>. Collected here (not in the DI
    /// container) so <c>AddWincheStorage</c> can build an engine from this package's rules only.
    /// </summary>
    internal List<RuleSet> Rulesets { get; } = [];

    /// <summary>
    /// Adds a <see cref="RuleSet"/> to this package's rules guard. Multiple calls accumulate — each
    /// ruleset's blocks are OR-combined. With no <c>UseRules</c> call, access is default-deny.
    /// </summary>
    public WincheStorageOptions UseRules(RuleSet ruleset)
    {
        Rulesets.Add(ruleset);
        return this;
    }

    /// <summary>Builds a <see cref="RuleSet"/> from a builder delegate and adds it to the merged set.</summary>
    public WincheStorageOptions UseRules(Action<RulesetBuilder> configure)
    {
        Rulesets.Add(RulesetBuilder.Build(configure));
        return this;
    }

    /// <summary>
    /// Registers file lifecycle hooks via a fluent builder, binding each hook to a Firestore-style
    /// path pattern at registration time. Multiple calls accumulate — each binding is registered as a
    /// singleton so that <c>GetServices&lt;HookRegistration&gt;()</c> returns them all at startup.
    /// </summary>
    public WincheStorageOptions UseHooks(Action<HookBuilder> configure)
    {
        var builder = new HookBuilder(Services);
        configure(builder);
        return this;
    }

    /// <summary>
    /// Enables the background orphan sweep: a hosted service that periodically reconciles the archive
    /// against the database and deletes archive objects with no matching row that are older than the
    /// grace window. Requires a real archive (e.g. <c>UseS3Archive</c>); the default null archive
    /// throws. See <see cref="OrphanSweepOptions"/> for interval, grace window, and prefix defaults.
    /// </summary>
    public WincheStorageOptions UseOrphanSweep(Action<OrphanSweepOptions>? configure = null)
    {
        Services.Configure(configure ?? (_ => { }));
        Services.AddHostedService<Services.OrphanSweepService>();
        return this;
    }
}

/// <summary>
/// Fluent builder used inside <see cref="WincheStorageOptions.UseHooks(Action{HookBuilder})"/>.
/// Each <see cref="Add{THook}(string)"/> registers one <see cref="Abstraction.HookRegistration"/> as a
/// singleton so that <c>GetServices&lt;HookRegistration&gt;()</c> returns them all at startup.
/// </summary>
public sealed class HookBuilder(IServiceCollection services)
{
    /// <summary>
    /// Registers a hook <typeparamref name="THook"/> (constructed via DI, so constructor injection
    /// works) bound to <paramref name="path"/>. The same hook type may be bound to multiple paths.
    /// </summary>
    public HookBuilder Add<THook>(string path) where THook : Abstraction.FileStoreHook
    {
        services.TryAddSingleton<THook>();
        services.AddSingleton(sp => new Abstraction.HookRegistration(path, sp.GetRequiredService<THook>()));
        return this;
    }

    /// <summary>Registers a pre-constructed <paramref name="hook"/> instance bound to <paramref name="path"/>.</summary>
    public HookBuilder Add(string path, Abstraction.FileStoreHook hook)
    {
        services.AddSingleton(new Abstraction.HookRegistration(path, hook));
        return this;
    }
}
