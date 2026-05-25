namespace Winche.Storage.Models;

public sealed class FileRecordNotFoundException(string path)
    : Exception($"File not found at '{path}'");
