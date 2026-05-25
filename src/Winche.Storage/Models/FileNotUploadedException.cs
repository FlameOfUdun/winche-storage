namespace Winche.Storage.Models;

public sealed class FileNotUploadedException(string path)
    : Exception($"File not uploaded at '{path}'");
