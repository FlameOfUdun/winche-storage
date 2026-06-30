using Winche.Storage.Models;

namespace Winche.Storage.Infrastructure;

/// <summary>
/// Pure decision logic for the orphan sweep. An archived object is an orphan — safe to delete —
/// only when it has <em>no</em> matching <c>winche_files</c> row <em>and</em> it is older than the
/// grace cutoff. The grace window protects in-flight uploads: an object freshly PUT to the archive
/// whose database row has not been committed yet is newer than the cutoff and is therefore kept.
/// </summary>
public static class OrphanSelector
{
    /// <summary>
    /// Returns the keys of <paramref name="objects"/> that are orphans relative to
    /// <paramref name="knownPaths"/> and the <paramref name="cutoffUtc"/> (typically
    /// <c>UtcNow - graceWindow</c>). Input order is preserved.
    /// </summary>
    public static IReadOnlyList<string> SelectOrphans(
        IEnumerable<ArchivedObject> objects,
        ISet<string> knownPaths,
        DateTime cutoffUtc)
    {
        var orphans = new List<string>();
        foreach (var obj in objects)
        {
            if (obj.LastModifiedUtc < cutoffUtc && !knownPaths.Contains(obj.Key))
                orphans.Add(obj.Key);
        }
        return orphans;
    }
}
