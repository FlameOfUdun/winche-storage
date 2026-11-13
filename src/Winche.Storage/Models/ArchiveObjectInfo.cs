namespace Winche.Storage.Models;

/// <summary>What an archive knows about a stored object: its size in bytes and its ETag (content fingerprint).</summary>
public readonly record struct ArchiveObjectInfo(long Size, string? ETag);
