namespace Winche.Storage.Models;

public sealed class InvalidUploadStatusException(string path, UploadStatus expected, UploadStatus current)
    : Exception($"Invalid upload status at '{path}'. Expected '{expected}', got '{current}'");
