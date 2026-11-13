namespace Winche.Storage.Locations;

/// <summary>
/// The settings area <c>storage.location</c>: where files are kept, and a move to somewhere else while one is under
/// way. Changed only through the console's guided steps (start, switch, finish, cancel), never by a plain save.
/// </summary>
public sealed record StorageLocationSettings
{
    public const string Area = "storage.location";

    /// <summary>The location in use; null for the one in the app's code.</summary>
    public ArchiveLocation? Active { get; init; }

    /// <summary>A move under way, if any.</summary>
    public StorageMove? Move { get; init; }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Active is { } a) problems.AddRange(a.Problems());
        if (Move is { } m)
        {
            if (m.Phase is not (StorageMove.Copying or StorageMove.Ready or StorageMove.Switched)) problems.Add($"'{m.Phase}' isn't a move phase.");
            if (m.To is null && m.Phase != StorageMove.Switched) problems.Add("A move needs somewhere to go.");
            if (m.To is { } to) problems.AddRange(to.Problems());
            if (m.Phase == StorageMove.Switched && Active?.Id != m.To?.Id) problems.Add("After the switch, the new location is the one in use.");
        }
        return problems;
    }
}

/// <summary>A place files are kept.</summary>
public sealed record ArchiveLocation
{
    /// <summary>Its id: names its secret (storage.location.{id}.secretKey).</summary>
    public string Id { get; init; } = "";
    /// <summary>"code" (the app's own) or a provider's kind, e.g. "s3".</summary>
    public string Kind { get; init; } = "s3";
    public S3LocationSettings? S3 { get; init; }

    public string SecretName => $"storage.location.{Id}.secretKey";

    public string Describe() => Kind switch
    {
        "code" => "the app's own location",
        "s3" when S3 is { } s => $"bucket {s.Bucket}{(string.IsNullOrEmpty(s.ServiceUrl) ? $" ({s.Region})" : $" at {s.ServiceUrl}")}",
        _ => Kind,
    };

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 40 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) problems.Add("A location needs an id (letters, digits, -).");
        if (Kind == "s3")
        {
            if (S3 is null) problems.Add("Fill in the bucket.");
            else
            {
                if (string.IsNullOrWhiteSpace(S3.Bucket)) problems.Add("Give the bucket's name.");
                if (string.IsNullOrWhiteSpace(S3.Region)) problems.Add("Give the bucket's region (e.g. eu-west-2; auto for R2).");
                if (S3.ServiceUrl is { Length: > 0 } u && !Uri.TryCreate(u, UriKind.Absolute, out _)) problems.Add("The service address must be a full address.");
            }
        }
        else if (Kind != "code") problems.Add($"'{Kind}' isn't a kind of location.");
        return problems;
    }
}

/// <summary>An S3 or S3-compatible bucket (AWS, Cloudflare R2, MinIO, Backblaze B2, …). The secret key is kept apart.</summary>
public sealed record S3LocationSettings
{
    public string Bucket { get; init; } = "";
    public string Region { get; init; } = "";
    /// <summary>For S3-compatible services: their address. Empty for AWS.</summary>
    public string? ServiceUrl { get; init; }
    public string? AccessKey { get; init; }
    public bool ForcePathStyle { get; init; }
}

/// <summary>A move between locations: copy everything, then switch, then finish.</summary>
public sealed record StorageMove
{
    public const string Copying = "copying";
    public const string Ready = "ready";
    public const string Switched = "switched";

    public string Id { get; init; } = "";
    /// <summary>copying (the old location still in use), ready (all copied), switched (the new one in use; reads fall back).</summary>
    public string Phase { get; init; } = Copying;
    /// <summary>Where files are copied from; null for the app's own location.</summary>
    public ArchiveLocation? From { get; init; }
    public ArchiveLocation? To { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    /// <summary>When the app switched to the new location (phase switched).</summary>
    public DateTimeOffset? SwitchedAt { get; init; }
}
