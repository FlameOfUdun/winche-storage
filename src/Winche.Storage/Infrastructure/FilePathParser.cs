namespace Winche.Storage.Infrastructure;

internal readonly record struct PathInfo(string Directory, string? Id);

internal static class FilePathParser
{
    public static PathInfo Parse(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            throw new ArgumentException("Path cannot be null or empty.", nameof(fullPath));

        var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 1)
            return new PathInfo(string.Empty, segments[0]);

        var directory = string.Join('/', segments.Take(segments.Length - 1));
        var id = segments.LastOrDefault();
        return new PathInfo(directory, id);
    }
    /// <summary>The longest path, in UTF-8 bytes: room for the generation in its archive key within S3's 1,024.</summary>
    public const int MaxPathBytes = 1000;

    /// <summary>
    /// Whether <paramref name="path"/> is a file path in its one accepted form: segments joined by single <c>/</c>, none
    /// empty (so no leading, trailing or doubled <c>/</c>), none <c>.</c> or <c>..</c>, no <c>\</c> or control
    /// characters, at most <see cref="MaxPathBytes"/> bytes. One spelling per file, so rules, the database and every
    /// archive agree on which file a path means.
    /// </summary>
    public static bool IsValidPath(string? path, out string? error)
    {
        error = path switch
        {
            null or "" => "it's empty",
            _ when System.Text.Encoding.UTF8.GetByteCount(path) > MaxPathBytes => $"it's longer than {MaxPathBytes} bytes",
            _ when path.Any(c => char.IsControl(c)) => "it has a control character",
            _ when path.Contains('\\') => "it has a backslash",
            _ when path.Split('/').Any(s => s.Length == 0) => "it has an empty segment (a leading, trailing or doubled '/')",
            _ when path.Split('/').Any(s => s is "." or "..") => "it has a '.' or '..' segment",
            _ => null,
        };
        return error is null;
    }

    /// <summary>Throws an <c>invalid-argument</c> <see cref="Models.StorageException"/> unless <paramref name="path"/> is valid.</summary>
    public static void Require(string? path)
    {
        if (!IsValidPath(path, out var error)) throw Models.StorageException.InvalidPath(path ?? "", error!);
    }
}