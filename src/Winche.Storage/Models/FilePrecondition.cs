namespace Winche.Storage.Models;

/// <summary>
/// What must hold for a write to go ahead, as with a database document: <see cref="Exists"/> false for create-only
/// (fails with <c>already-exists</c>), true for update-only; <see cref="Version"/> for "only if nobody changed it since"
/// (fails with <c>failed-precondition</c>).
/// </summary>
public sealed record FilePrecondition(bool? Exists = null, long? Version = null)
{
    /// <summary>Only if there's no file at the path yet.</summary>
    public static FilePrecondition IfNotExists { get; } = new(Exists: false);

    internal void Check(string path, FileRecord? current)
    {
        if (Exists == false && current is not null) throw StorageException.AlreadyExists(path);
        if (Exists == true && current is null) throw StorageException.NotThere(path);
        if (Version is { } v)
        {
            if (current is null) throw StorageException.NotThere(path);
            if (current.Version != v) throw StorageException.VersionMismatch(path, v, current.Version);
        }
    }
}
