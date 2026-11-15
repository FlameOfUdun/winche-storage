using System.Text.Json;
using Npgsql;
using Winche.Core.Feeds;
using Winche.Storage.Abstraction;
using Winche.Storage.Constants;
using Winche.Storage.Models;

namespace Winche.Storage.Services;

/// <summary>What happened to a file, as recorded in the feed.</summary>
internal static class HookEventKind
{
    public const string Registered = "registered";
    public const string UploadConfirmed = "upload_confirmed";
    public const string MetadataUpdated = "metadata_updated";
    public const string Deleted = "deleted";
}

/// <summary>
/// Storage's change feed (Winche.Core's <see cref="WincheEventFeed"/>, named <see cref="Name"/>): every file change is
/// written in its own transaction, and each hook is a durable consumer of it with its own cursor.
/// </summary>
internal static class StorageFeed
{
    public const string Name = "storage";

    public static WincheFeed<FeedEvent> Open(NpgsqlDataSource db) => WincheEventFeed.Open(db, Name, WincheStorageTables.Feed);

    /// <summary>Records a file change in <paramref name="tx"/>, with the file as it is now (null when deleted).</summary>
    public static Task AppendAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string kind, string path, FileRecord? record, CancellationToken ct) =>
        WincheEventFeed.AppendAsync(conn, tx, WincheStorageTables.Feed, kind, path,
            record is null ? null : JsonSerializer.SerializeToNode(record)!.AsObject(), ct);

    /// <summary>A hook's consumer name, stable across restarts and nodes: its type and path pattern.</summary>
    public static string ConsumerOf(HookRegistration r) => $"hooks:{r.Hook.GetType().FullName}|{r.Path}";
}
