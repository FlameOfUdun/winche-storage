namespace Winche.Storage.DependencyInjection;

/// <summary>
/// Configures the background orphan sweep enabled via <see cref="WincheStorageOptions.UseOrphanSweep"/>.
/// The sweep reconciles the archive against the database, deleting archive objects that have no
/// matching <c>winche_files</c> row and are older than <see cref="GraceWindow"/>.
/// </summary>
public sealed class OrphanSweepOptions
{
    /// <summary>How often the sweep runs. Default: every 6 hours.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// An unreferenced archive object must be older than this before it is treated as an orphan.
    /// Protects in-flight uploads (object PUT, database row not committed yet). Default: 24 hours.
    /// </summary>
    public TimeSpan GraceWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Optional key prefix to scope the sweep. Null/empty sweeps the whole bucket.</summary>
    public string? Prefix { get; set; }
}
