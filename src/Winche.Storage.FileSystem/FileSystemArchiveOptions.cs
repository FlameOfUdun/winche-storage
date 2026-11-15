namespace Winche.Storage.FileSystem;

/// <summary>Where <see cref="FileSystemArchive"/> keeps files, and how its URLs are signed.</summary>
public sealed class FileSystemArchiveOptions
{
    /// <summary>The folder files live in (made if missing): a local disk, a mounted volume, a network share.</summary>
    public string Root { get; set; } = "";

    /// <summary>
    /// The secret that signs upload and download URLs (32+ characters, from configuration or a secret store). Every
    /// instance serving the files needs the same one. When not set, the key Winche.Core derives from the project's
    /// master key (<c>AddWincheConfig(o =&gt; o.MasterKey = …)</c>) is used.
    /// </summary>
    public string SigningKey { get; set; } = "";

    /// <summary>
    /// The app's public base address the URLs start with, e.g. <c>https://api.example.com</c>. When not set, the address
    /// of the request asking for the URL (behind a proxy, use ASP.NET Core's forwarded headers).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Where <c>MapWincheStorageFileSystem()</c> serves the files (default <c>/_files</c>).</summary>
    public string RoutePrefix { get; set; } = "/_files";

    /// <summary>How long an upload or download URL works (default one hour).</summary>
    public TimeSpan UrlLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The largest single part of a multipart upload (default 512 MB).</summary>
    public long MaxPartBytes { get; set; } = 512L * 1024 * 1024;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Root)) throw new InvalidOperationException("FileSystemArchive: set Root, the folder files live in.");
        if (SigningKey.Length < 32)
            throw new InvalidOperationException("FileSystemArchive: set SigningKey (32+ characters), or the project's master key in AddWincheConfig.");
        if (!RoutePrefix.StartsWith('/') || RoutePrefix.EndsWith('/')) throw new InvalidOperationException("FileSystemArchive: RoutePrefix looks like /_files.");
        if (PublicBaseUrl is not null && !Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("FileSystemArchive: PublicBaseUrl must be an absolute address.");
    }
}
