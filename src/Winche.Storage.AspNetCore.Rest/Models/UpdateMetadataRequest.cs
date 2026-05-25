using System.Text.Json.Nodes;

namespace Winche.Storage.AspNetCore.Rest.Models
{
    public sealed record SetMetadataRequest(JsonObject Metadata);
}
