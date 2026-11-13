using System.Text.Json.Nodes;

namespace Winche.Storage.AspNetCore.Rest.Models
{
    /// <param name="Metadata">Keys to set; a null value removes the key.</param>
    /// <param name="ContentDisposition">Null leaves it, <c>""</c> clears it.</param>
    /// <param name="CacheControl">Null leaves it, <c>""</c> clears it.</param>
    /// <param name="Precondition">E.g. <c>{"version": n}</c>: only if the file is still at that version.</param>
    public sealed record SetMetadataRequest(JsonObject? Metadata, string? ContentDisposition = null, string? CacheControl = null,
        Winche.Storage.Models.FilePrecondition? Precondition = null);
}
