using System.Text.Json.Nodes;

namespace Winche.Storage.AspNetCore.Rest.Models
{
    /// <param name="Precondition">Optional, as for a database document: <c>{"exists": false}</c> to only create, <c>{"version": n}</c> to only replace that version.</param>
    /// <param name="ContentDisposition">How downloads are offered, e.g. <c>attachment; filename="a.pdf"</c>.</param>
    /// <param name="CacheControl">How long downloads may be cached, e.g. <c>public, max-age=3600</c>.</param>
    public sealed record CreateFileRequest(string MimeType, long SizeBytes, JsonObject? Metadata,
        Winche.Storage.Models.FilePrecondition? Precondition = null, string? ContentDisposition = null, string? CacheControl = null);
}
