namespace Winche.Storage.Models;

/// <summary>
/// A storage failure with its shared Winche error (<see cref="WincheError"/>): what the REST layer answers with as is.
/// </summary>
public sealed class StorageException(WincheError error) : Exception(error.Message)
{
    /// <summary>The error to answer with.</summary>
    public WincheError Error { get; } = error;

    internal static StorageException AlreadyExists(string path) =>
        new(new WincheError(WincheErrorCode.AlreadyExists, $"There's already a file at {path}."));

    internal static StorageException VersionMismatch(string path, long expected, long actual) =>
        new(new WincheError(WincheErrorCode.FailedPrecondition, $"The file at {path} is at version {actual}, not {expected}.", "version"));

    internal static StorageException NotThere(string path) =>
        new(new WincheError(WincheErrorCode.FailedPrecondition, $"There's no file at {path}.", "not-found"));

    internal static StorageException Changed(string path) =>
        new(new WincheError(WincheErrorCode.Aborted, $"The file at {path} changed meanwhile; try again."));

    internal static StorageException InvalidPath(string path, string why) =>
        new(new WincheError(WincheErrorCode.InvalidArgument, $"'{path}' isn't a valid file path: {why}.", "invalid-path"));

    /// <summary>A multipart upload the archive no longer has (aborted, completed, or expired).</summary>
    public static StorageException UploadGone(string path) =>
        new(new WincheError(WincheErrorCode.FailedPrecondition, $"The upload of {path} is no longer there; start it again.", "upload-gone"));

    internal static StorageException NoUpload(string path) =>
        new(new WincheError(WincheErrorCode.FailedPrecondition, $"No multipart upload of {path} is in progress; start from part 1.", "no-upload"));
}
