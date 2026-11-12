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
    /// Largest file a client may register, in bytes; null (the default) means no limit beyond the
    /// archive's own. A larger declared size is refused when the record is created (413 over REST), and
    /// a multipart upload whose parts don't add up to the declared size is refused at confirmation, so
    /// the limit holds for the bytes actually stored. For per-path limits, write a create rule on
    /// <c>request.resource.sizeBytes</c>.
    /// </summary>
    public long? MaxFileSizeBytes { get; set; }

    /// <summary>What's logged about requests (<see cref="LogRequests"/>), or null when off.</summary>
    public Logging.RequestLogOptions? RequestLog { get; internal set; }

    /// <summary>
    /// Lets the console change the file size limit, request logging and the orphan sweep without a deploy (9.1.0);
    /// what's set here becomes the starting values. Needs <c>AddWincheConfig(…)</c> to save changes.
    /// </summary>
    public WincheStorageOptions UseSettings()
    {
        SettingsEnabled = true;
        return this;
    }

    /// <summary>Whether the console may change these settings (<see cref="UseSettings"/>).</summary>
    public bool SettingsEnabled { get; private set; }

    /// <summary>Whether <see cref="UseOrphanSweep"/> was called in code.</summary>
    internal bool OrphanSweepInCode { get; private set; }

    /// <summary>
    /// Log app requests that go through the access rules (9.1.0), as structured entries in the category
    /// <c>Winche.Storage.Requests</c> (<c>Operation</c>, <c>Path</c>, <c>Principal</c>, <c>Outcome</c>, <c>LatencyMs</c>):
    /// denied and slow ones always (Warning), a sample of the rest (Information).
    /// </summary>
    public WincheStorageOptions LogRequests(Action<Logging.RequestLogOptions>? configure = null)
    {
        var o = new Logging.RequestLogOptions();
        configure?.Invoke(o);
        RequestLog = o;
        return this;
    }

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

    /// <summary>
    /// Parses <paramref name="rules"/> (the rules language, as in the console's editor) and adds it like
    /// <see cref="UseRules(RuleSet)"/>. A syntax error throws here, at startup.
    /// </summary>
    public WincheStorageOptions UseRulesText(string rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rulesets.Add(RuleFiles.Parse(rules, source: null));
        return this;
    }

    /// <summary>
    /// Reads and parses a rules file (e.g. <c>storage.rules</c>) and adds it like <see cref="UseRules(RuleSet)"/>.
    /// A relative <paramref name="path"/> is resolved against <see cref="AppContext.BaseDirectory"/>. A missing
    /// file or a syntax error throws here, at startup, naming the file, line and column.
    /// </summary>
    public WincheStorageOptions UseRulesFile(string path)
    {
        Rulesets.Add(RuleFiles.Load(path));
        return this;
    }

    /// <summary>
    /// Registers file lifecycle hooks via a fluent builder, binding each hook to a Firestore-style
    /// path pattern at registration time. Multiple calls accumulate — each binding is registered as a
    /// singleton so that <c>GetServices&lt;HookRegistration&gt;()</c> returns them all at startup.
    /// </summary>
    /// <summary>
    /// How file hooks are delivered (retries, lease, dead letters). File changes reach their hooks durably and at
    /// least once; see <see cref="Models.HookDeliveryOptions"/>.
    /// </summary>
    public Models.HookDeliveryOptions HookDelivery { get; set; } = new();

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
        OrphanSweepInCode = true;
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
