using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Storage.Models;

namespace Winche.Storage.Authorization;

/// <summary>
/// Converts a <see cref="FileRecord"/> to the rules engine's <c>resource</c> map.
/// System fields (<c>id</c>, <c>path</c>, <c>directory</c>, <c>mimeType</c>, <c>sizeBytes</c>,
/// <c>uploadStatus</c>, <c>version</c>, <c>createdAt</c>, <c>updatedAt</c>) are exposed at the
/// top level; the <c>metadata</c> JSON object is nested under <c>"metadata"</c>.
/// </summary>
internal static class FileToResource
{
    public static RuleValue Convert(FileRecord file) =>
        RuleValue.Map(new Dictionary<string, RuleValue>
        {
            ["id"] = RuleValue.String(file.Id),
            ["path"] = RuleValue.String(file.Path),
            ["directory"] = RuleValue.String(file.Directory),
            ["mimeType"] = RuleValue.String(file.MimeType),
            ["sizeBytes"] = RuleValue.Int(file.SizeBytes),
            ["uploadStatus"] = RuleValue.String(file.UploadStatus.ToString().ToLowerInvariant()),
            ["version"] = RuleValue.Int(file.Version),
            ["createdAt"] = RuleValue.Timestamp(new DateTimeOffset(file.CreatedAt, TimeSpan.Zero)),
            ["updatedAt"] = RuleValue.Timestamp(new DateTimeOffset(file.UpdatedAt, TimeSpan.Zero)),
            ["metadata"] = ConvertJsonNode(file.MetaData),
        });

    private static RuleValue ConvertJsonNode(JsonNode? node) => node switch
    {
        null => RuleValue.Null,
        JsonValue v when v.TryGetValue<bool>(out var b) => RuleValue.Bool(b),
        JsonValue v when v.TryGetValue<long>(out var l) => RuleValue.Int(l),
        JsonValue v when v.TryGetValue<int>(out var i) => RuleValue.Int(i),
        JsonValue v when v.TryGetValue<double>(out var d) => RuleValue.Double(d),
        JsonValue v when v.TryGetValue<string>(out var s) => RuleValue.String(s!),
        JsonObject obj => RuleValue.Map(obj.ToDictionary(kv => kv.Key, kv => ConvertJsonNode(kv.Value))),
        JsonArray arr => RuleValue.List(arr.Select(ConvertJsonNode).ToList()),
        _ => RuleValue.String(node.ToString()),
    };
}
