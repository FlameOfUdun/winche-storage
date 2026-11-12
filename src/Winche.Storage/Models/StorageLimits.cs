namespace Winche.Storage.Models;

/// <summary>Size limits enforced by <see cref="Services.FileStorage"/>; set through <c>WincheStorageOptions</c>.</summary>
public sealed record StorageLimits(long? MaxFileSizeBytes)
{
    /// <summary>The largest file allowed, or null for no limit. Changes from the console apply to the next upload.</summary>
    public long? MaxFileSizeBytes { get; internal set; } = MaxFileSizeBytes;
}

/// <summary>A file was declared larger than <see cref="StorageLimits.MaxFileSizeBytes"/> (or with a negative size).</summary>
public sealed class FileTooLargeException(string path, long sizeBytes, long? maxBytes)
    : Exception(sizeBytes < 0
        ? $"Invalid size {sizeBytes} for '{path}'"
        : $"File '{path}' is {sizeBytes} bytes; the limit is {maxBytes}")
{
    public long SizeBytes { get; } = sizeBytes;
    public long? MaxBytes { get; } = maxBytes;
}

/// <summary>
/// The bytes uploaded don't add up to the size the file was registered with. The partial upload is
/// discarded; the record stays pending so the client can upload again.
/// </summary>
public sealed class UploadSizeMismatchException(string path, long declared, long actual)
    : Exception($"Upload for '{path}' is {actual} bytes but was registered as {declared}")
{
    public long Declared { get; } = declared;
    public long Actual { get; } = actual;
}
