namespace Winche.Storage.Infrastructure;

public readonly record struct PathInfo(string Directory, string? Id);

public static class FilePathParser
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

    public static bool IsValidPath(string path, out string? error)
    {
        if (string.IsNullOrEmpty(path))
        {
            error = "Path cannot be null or empty.";
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "Path must contain at least one segment.";
            return false;
        }

        error = null;
        return true;
    }
}