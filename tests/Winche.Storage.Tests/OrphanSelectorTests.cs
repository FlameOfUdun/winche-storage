using Winche.Storage.Infrastructure;
using Winche.Storage.Models;
using Xunit;

namespace Winche.Storage.Tests;

public class OrphanSelectorTests
{
    // Fixed reference point so tests never touch the wall clock.
    private static readonly DateTime Cutoff = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

    private static ArchivedObject Obj(string key, DateTime modifiedUtc) => new(key, modifiedUtc);

    [Fact]
    public void Unreferenced_object_older_than_cutoff_is_an_orphan()
    {
        var objects = new[] { Obj("a/1", Cutoff.AddHours(-1)) };

        var orphans = OrphanSelector.SelectOrphans(objects, new HashSet<string>(), Cutoff);

        Assert.Equal(new[] { "a/1" }, orphans);
    }

    [Fact]
    public void Object_with_matching_row_is_never_an_orphan()
    {
        var objects = new[] { Obj("a/1", Cutoff.AddYears(-1)) };

        var orphans = OrphanSelector.SelectOrphans(objects, new HashSet<string> { "a/1" }, Cutoff);

        Assert.Empty(orphans);
    }

    [Fact]
    public void Unreferenced_object_within_grace_window_is_kept()
    {
        // Newer than the cutoff => still inside the grace window => may be an in-flight upload.
        var objects = new[] { Obj("a/1", Cutoff.AddMinutes(1)) };

        var orphans = OrphanSelector.SelectOrphans(objects, new HashSet<string>(), Cutoff);

        Assert.Empty(orphans);
    }

    [Fact]
    public void Returns_only_old_unreferenced_keys_preserving_order()
    {
        var objects = new[]
        {
            Obj("orphan-old", Cutoff.AddHours(-1)),   // purge: old + unreferenced
            Obj("known-old", Cutoff.AddHours(-1)),    // keep: referenced
            Obj("orphan-fresh", Cutoff.AddHours(1)),  // keep: within grace
            Obj("orphan-old-2", Cutoff.AddDays(-5)),  // purge
        };

        var orphans = OrphanSelector.SelectOrphans(objects, new HashSet<string> { "known-old" }, Cutoff);

        Assert.Equal(new[] { "orphan-old", "orphan-old-2" }, orphans);
    }
}
