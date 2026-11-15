namespace Winche.Storage.Locations;

/// <summary>
/// Reading and writing an archive's bytes from the server, for moving files between locations. Archives that can't
/// (a custom one) can still be used, but files can't be moved into or out of them.
/// </summary>
public interface IArchiveContent
{
    /// <summary>The object's bytes, or null when there's no such object.</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>Writes (or replaces) the object.</summary>
    Task WriteAsync(string path, Stream content, string? contentType, CancellationToken ct = default);
}

/// <summary>Builds archives for locations chosen in the console (registered by a package, e.g. Winche.Storage.S3).</summary>
public interface IArchiveProvider
{
    /// <summary>The location kind it builds, e.g. "s3".</summary>
    string Kind { get; }

    Interfaces.IArchive Create(ArchiveLocation location, string? secretKey);
}
