using System.Text.Json.Nodes;

namespace Winche.Storage.AspNetCore.Rest.Models
{
    /// <param name="Precondition">Optional, as for a database document: <c>{"exists": false}</c> to only create, <c>{"version": n}</c> to only replace that version.</param>
    public sealed record CreateFileRequest(string MimeType, long SizeBytes, JsonObject? Metadata, Winche.Storage.Models.FilePrecondition? Precondition = null);
}
