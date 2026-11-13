using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Storage.Infrastructure;
using Winche.Storage.Models;
using Winche.Values;

namespace Winche.Storage.Authorization;

/// <summary>
/// Converts a <see cref="FileRecord"/> to the rules engine's <c>resource</c> map.
/// System fields (<c>id</c>, <c>path</c>, <c>directory</c>, <c>mimeType</c>, <c>sizeBytes</c>,
/// <c>uploadStatus</c>, <c>version</c>, <c>createdAt</c>, <c>updatedAt</c>) are exposed at the
/// top level; the <c>metadata</c> JSON object is nested under <c>"metadata"</c>.
/// </summary>
internal static class FileToResource
{
    public static MapValue Convert(FileRecord file) =>
        new MapValue(new Dictionary<string, Value>
        {
            ["id"] = new StringValue(file.Id),
            ["path"] = new StringValue(file.Path),
            ["directory"] = new StringValue(file.Directory),
            ["mimeType"] = new StringValue(file.MimeType),
            ["sizeBytes"] = new IntegerValue(file.SizeBytes),
            ["uploadStatus"] = new StringValue(file.UploadStatus.ToString().ToLowerInvariant()),
            ["version"] = new IntegerValue(file.Version),
            ["createdAt"] = new TimestampValue(new DateTimeOffset(file.CreatedAt, TimeSpan.Zero)),
            ["updatedAt"] = new TimestampValue(new DateTimeOffset(file.UpdatedAt, TimeSpan.Zero)),
            ["metadata"] = PlainJson.ToValue(file.MetaData),
        });

    /// <summary>
    /// <c>request.resource</c> for a file being registered: <c>path</c>, <c>directory</c>, <c>mimeType</c>,
    /// <c>sizeBytes</c> and <c>metadata</c> (the fields a client supplies).
    /// </summary>
    public static MapValue Incoming(string path, string mimeType, long sizeBytes, JsonObject? metadata)
    {
        return new MapValue(new Dictionary<string, Value>
        {
            ["path"] = new StringValue(path),
            ["directory"] = new StringValue(FilePathParser.Parse(path).Directory),
            ["mimeType"] = new StringValue(mimeType),
            ["sizeBytes"] = new IntegerValue(sizeBytes),
            ["metadata"] = PlainJson.ToValue(metadata),
        });
    }
}
