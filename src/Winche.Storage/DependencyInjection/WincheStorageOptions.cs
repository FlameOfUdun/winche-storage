using Microsoft.Extensions.DependencyInjection;
using Winche.Rules;
using Winche.Storage.Abstraction;

namespace Winche.Storage.DependencyInjection;

/// <summary>
/// The single options surface for Winche.Storage: connection, store behavior, and component
/// registrations (rules, hooks). Configured through the <c>AddWincheStorage</c> lambda and consumed
/// at runtime via <c>IOptions&lt;WincheStorageOptions&gt;</c>. Satellite packages extend this type
/// (e.g. <c>AddS3Archive</c>, <c>MapClaims</c>) through <see cref="Services"/>.
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

    /// <summary>Table name for file metadata. Defaults to "files".</summary>
    public string TableName { get; set; } = "files";

    /// <summary>
    /// Adds a <see cref="RuleSet"/> to the Winche.Rules guard. Multiple calls accumulate — each
    /// ruleset's blocks are OR-combined. With no <c>UseRules</c> call, access is default-deny.
    /// </summary>
    public WincheStorageOptions UseRules(RuleSet ruleset)
    {
        Services.AddSingleton(ruleset);
        return this;
    }

    /// <summary>Builds a <see cref="RuleSet"/> from a builder delegate and adds it to the merged set.</summary>
    public WincheStorageOptions UseRules(Action<RulesetBuilder> configure)
    {
        Services.AddSingleton(RulesetBuilder.Build(configure));
        return this;
    }

    public WincheStorageOptions AddFileStoreHook<THook>() where THook : FileStoreHook
    {
        Services.AddSingleton<FileStoreHook, THook>();
        return this;
    }
}
