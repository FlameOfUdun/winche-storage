using Winche.Storage.DependencyInjection;
using Winche.Storage.Models;

namespace Winche.Storage.Settings;

/// <summary>The settings area <c>storage</c> (<c>o.UseSettings()</c>): size limit, request logging and the orphan sweep.</summary>
public sealed record StorageSettings
{
    public const string Area = "storage";

    /// <summary>The largest file allowed, in MB; null for no limit.</summary>
    public double? MaxFileSizeMb { get; init; }
    public StorageRequestLogSettings RequestLog { get; init; } = new();
    public OrphanSweepSettings OrphanSweep { get; init; } = new();

    public static StorageSettings From(WincheStorageOptions o, OrphanSweepOptions sweep, bool sweepOn) => new()
    {
        MaxFileSizeMb = o.MaxFileSizeBytes is { } b ? Math.Round(b / 1048576.0, 3) : null,
        RequestLog = o.RequestLog is { } r ? new() { Enabled = true, SampleRate = r.SuccessSampleRate, SlowMs = (int)r.SlowThreshold.TotalMilliseconds } : new(),
        OrphanSweep = new()
        {
            Enabled = sweepOn, IntervalHours = sweep.Interval.TotalHours, GraceHours = sweep.GraceWindow.TotalHours, Prefix = sweep.Prefix,
        },
    };

    internal void ApplyTo(WincheStorageOptions o, StorageLimits limits, OrphanSweepOptions sweep)
    {
        o.MaxFileSizeBytes = limits.MaxFileSizeBytes = MaxFileSizeMb is { } mb ? (long)(mb * 1048576) : null;
        o.RequestLog = RequestLog.Enabled
            ? new Logging.RequestLogOptions { SuccessSampleRate = RequestLog.SampleRate, SlowThreshold = TimeSpan.FromMilliseconds(RequestLog.SlowMs) }
            : null;
        sweep.Enabled = OrphanSweep.Enabled;
        sweep.Interval = TimeSpan.FromHours(OrphanSweep.IntervalHours);
        sweep.GraceWindow = TimeSpan.FromHours(OrphanSweep.GraceHours);
        sweep.Prefix = string.IsNullOrWhiteSpace(OrphanSweep.Prefix) ? null : OrphanSweep.Prefix;
    }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (MaxFileSizeMb is <= 0 or > 5 * 1024 * 1024) problems.Add("The file size limit is more than 0 MB and at most 5 TB (or none).");
        if (RequestLog.Enabled && RequestLog.SampleRate is < 0 or > 1) problems.Add("Log between 0% and 100% of successful requests.");
        if (RequestLog.Enabled && RequestLog.SlowMs < 1) problems.Add("A slow request takes at least 1 ms.");
        if (OrphanSweep.IntervalHours is < 0.25 or > 24 * 30) problems.Add("Sweep every 15 minutes to 30 days.");
        if (OrphanSweep.GraceHours is < 1 or > 24 * 90) problems.Add("Leave unreferenced files 1 hour to 90 days before deleting them.");
        return problems;
    }
}

public sealed record StorageRequestLogSettings
{
    public bool Enabled { get; init; }
    public double SampleRate { get; init; } = 0.1;
    public int SlowMs { get; init; } = 1000;
}

public sealed record OrphanSweepSettings
{
    /// <summary>Delete stored files that no file record points to (left by interrupted uploads or deletes).</summary>
    public bool Enabled { get; init; }
    public double IntervalHours { get; init; } = 6;
    /// <summary>How old an unreferenced file must be before it's deleted.</summary>
    public double GraceHours { get; init; } = 24;
    /// <summary>Only under this folder; everywhere when empty.</summary>
    public string? Prefix { get; init; }
}
