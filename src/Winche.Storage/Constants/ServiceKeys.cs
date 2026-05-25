namespace Winche.Storage.Constants;

internal static class ServiceKeys
{
    public const string DATA_SOURCE_KEY = "FileManager";
    public const string CONFIG_SECTION_KEY = "WincheStorage";
    public const string S3_ARCHIVE_SECTION_KEY = $"{CONFIG_SECTION_KEY}:S3Archive";
}
