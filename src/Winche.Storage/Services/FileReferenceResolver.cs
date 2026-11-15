using System.Text.Json;
using System.Text.Json.Nodes;
using Winche.Core.Feeds;
using Winche.Rules;
using Winche.Storage.Authorization;
using Winche.Storage.Infrastructure;

namespace Winche.Storage.Services;

/// <summary>
/// <c>file:</c> for the database's <c>expand</c>: a document field holding a file's path (<c>scans/a.png</c>) comes back
/// with the file's record (type, size, metadata, upload status; never a download link, which expires: the app asks
/// storage for one). Read as the caller: a file the storage rules don't let them <c>get</c> stays a plain path.
/// </summary>
internal sealed class FileReferenceResolver(FileStorage files, RuleEngine engine) : IWincheReferenceResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => "file";

    /// <summary>Storage's own feed: any file change refreshes live subscriptions that expand files.</summary>
    public string? ChangeChannel => WincheEventFeed.Channel(Constants.WincheStorageTables.Feed);

    public async Task<IReadOnlyDictionary<string, JsonObject>> ResolveAsync(IReadOnlyList<string> keys, ReferenceCaller caller, CancellationToken ct)
    {
        Interfaces.IFileStorage reader = caller.Trusted ? files : new RuleGuardedFileStorage(files, engine, () => caller.Claims);
        var found = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var path = key.TrimStart('/');
            if (!FilePathParser.IsValidPath(path, out _)) continue;
            try
            {
                if (await reader.GetAsync(path, ct) is { } file)
                    found[key] = JsonSerializer.SerializeToNode(file, Json)!.AsObject();
            }
            catch (AccessDeniedException) { }   // not theirs to see: stays a path
        }
        return found;
    }
}
