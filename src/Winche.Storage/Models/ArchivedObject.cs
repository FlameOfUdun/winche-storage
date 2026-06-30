namespace Winche.Storage.Models;

/// <summary>
/// A single object as reported by the archive's listing API: its key (the storage path) and the
/// last-modified timestamp used by the orphan sweep's grace window. <paramref name="LastModifiedUtc"/>
/// is always UTC.
/// </summary>
public readonly record struct ArchivedObject(string Key, DateTime LastModifiedUtc);
