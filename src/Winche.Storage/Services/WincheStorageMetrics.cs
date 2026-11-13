using System.Diagnostics.Metrics;

namespace Winche.Storage.Services;

/// <summary>Winche.Storage's meter (<see cref="MeterName"/>), for OpenTelemetry: <c>.AddMeter(WincheStorageMetrics.MeterName)</c>.</summary>
public static class WincheStorageMetrics
{
    /// <summary>The meter's name.</summary>
    public const string MeterName = "Winche.Storage";

    internal static readonly Meter Meter = new(MeterName, typeof(WincheStorageMetrics).Assembly.GetName().Version?.ToString());

    internal static readonly Counter<long> FilesRegistered =
        Meter.CreateCounter<long>("winche.storage.files.registered", null, "Files registered (new or replacing one)");
    internal static readonly Counter<long> UploadsConfirmed =
        Meter.CreateCounter<long>("winche.storage.uploads.confirmed", null, "Uploads confirmed");
    internal static readonly Counter<long> UploadBytes =
        Meter.CreateCounter<long>("winche.storage.uploads.bytes", "By", "Bytes in confirmed uploads");
    internal static readonly Counter<long> UploadsRefused =
        Meter.CreateCounter<long>("winche.storage.uploads.refused", null, "Confirms refused: missing, wrong size, or changed meanwhile");
    internal static readonly Counter<long> FilesDeleted =
        Meter.CreateCounter<long>("winche.storage.files.deleted", null, "Files deleted");
    internal static readonly Counter<long> LinksSigned =
        Meter.CreateCounter<long>("winche.storage.links.signed", null, "Signed links handed out (tag kind: upload, part, download)");
    internal static readonly Counter<long> AccessDenied =
        Meter.CreateCounter<long>("winche.storage.access.denied", null, "Requests the rules refused (tag operation)");
    internal static readonly Counter<long> HookDeliveries =
        Meter.CreateCounter<long>("winche.storage.hook.deliveries", null, "File changes delivered to hooks");
    internal static readonly Counter<long> HookFailures =
        Meter.CreateCounter<long>("winche.storage.hook.failures", null, "Failed hook deliveries (retried)");
}
