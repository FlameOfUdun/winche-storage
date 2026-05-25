using System.Text.Json.Nodes;

namespace Winche.Storage.AspNetCore.Rest.Models
{
    public sealed record CreateFileRequest(string MimeType, long SizeBytes, JsonObject? Metadata);
}
