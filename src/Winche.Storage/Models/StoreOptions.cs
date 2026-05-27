namespace Winche.Storage.Models;

/// <summary>
/// Options for configuring the database store used by Winche.Storage.
/// </summary>
public sealed record StoreOptions
{
    /// <summary>
    /// The database schema to use for storing file metadata. Defaults to "public".
    /// </summary>
    public string Schema { get; set; } = "public";

    /// <summary>
    /// The name of the database table to use for storing file metadata. Defaults to "files".
    /// </summary>
    public string TableName { get; set; } = "files";

    /// <summary>
    /// Indicates whether the database schema and table should be automatically created if they do not exist. Defaults to true.
    /// </summary>
    public bool EnsureCreated { get; set; } = true;
}
