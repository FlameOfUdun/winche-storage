using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Winche.Storage.Authorization;
using Winche.Storage.Models;

namespace Winche.Storage.Logging;

/// <summary>What <see cref="DependencyInjection.WincheStorageOptions.LogRequests"/> records about each request.</summary>
public sealed class RequestLogOptions
{
    /// <summary>The share of successful requests logged, 0–1 (default 0.1). Denied and slow ones are always logged.</summary>
    public double SuccessSampleRate { get; set; } = 0.1;

    /// <summary>A request at least this slow is always logged, as a Warning (default 1 s).</summary>
    public TimeSpan SlowThreshold { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Logs app requests that go through the access rules (category <see cref="Category"/>) as structured entries —
/// <c>Operation</c>, <c>Path</c>, <c>Principal</c>, <c>Outcome</c>, <c>LatencyMs</c> — with a plain message. Denied and
/// slow requests always (Warning); a sample of successful ones (Information).
/// </summary>
internal sealed class RequestLog(ILogger logger, Func<RequestLogOptions?> current, Func<IReadOnlyDictionary<string, object?>?> claims)
{
    public RequestLog(ILogger logger, RequestLogOptions options, Func<IReadOnlyDictionary<string, object?>?> claims) : this(logger, () => options, claims) { }

    public const string Category = "Winche.Storage.Requests";

    public async Task<T> RunAsync<T>(string operation, string path, Func<Task<T>> body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await body();
            Done(operation, path, result is null ? "notFound" : "ok", watch);
            return result;
        }
        catch (AccessDeniedException)
        {
            Done(operation, path, "denied", watch);
            throw;
        }
        catch (FileRecordNotFoundException)
        {
            Done(operation, path, "notFound", watch);
            throw;
        }
        catch (Exception ex) when (ex is FileTooLargeException or InvalidUploadStatusException or UploadSizeMismatchException)
        {
            Done(operation, path, "invalid", watch);
            throw;
        }
    }

    private void Done(string operation, string path, string outcome, Stopwatch watch)
    {
        var ms = watch.ElapsedMilliseconds;
        if (current() is not { } options) return;   // logging turned off
        var slow = ms >= options.SlowThreshold.TotalMilliseconds;
        var level = outcome is "ok" or "notFound" && !slow ? LogLevel.Information : LogLevel.Warning;
        if (!logger.IsEnabled(level)) return;
        if (level == LogLevel.Information && Random.Shared.NextDouble() >= options.SuccessSampleRate) return;
        var c = claims();
        var principal = WincheCaller.UidOf(c) ?? "signed out";
        logger.Log(level, new EventId(outcome == "denied" ? 2 : slow ? 3 : 1, "Request"),
            new RequestEntry(operation, path, principal, outcome, ms, slow), null, static (e, _) => e.ToString());
    }
}

/// <summary>A request entry: structured values for log stores that keep them, a sentence for those that don't.</summary>
public sealed class RequestEntry(string operation, string path, string principal, string outcome, long latencyMs, bool slow)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    public string Operation => operation;
    public string Path => path;
    public string Principal => principal;
    public string Outcome => outcome;
    public long LatencyMs => latencyMs;

    private static readonly Dictionary<string, (string Verb, string Gerund)> Words = new(StringComparer.Ordinal)
    {
        ["storage.create"] = ("registered", "registering"),
        ["storage.get"] = ("read", "reading"),
        ["storage.update"] = ("updated", "updating"),
        ["storage.delete"] = ("deleted", "deleting"),
        ["storage.list"] = ("listed", "listing"),
        ["storage.upload"] = ("started uploading", "uploading"),
        ["storage.uploadPart"] = ("uploaded a part of", "uploading a part of"),
        ["storage.parts"] = ("listed the uploaded parts of", "listing the uploaded parts of"),
        ["storage.download"] = ("downloaded", "downloading"),
        ["storage.confirm"] = ("finished uploading", "finishing the upload of"),
    };

    public override string ToString()
    {
        var (verb, gerund) = Words.GetValueOrDefault(operation, (operation, operation));
        return outcome switch
        {
            "ok" when slow => $"{principal} {verb} {path}, which took {latencyMs:N0} ms (slow).",
            "ok" => $"{principal} {verb} {path} ({latencyMs:N0} ms).",
            "denied" => $"{principal} was denied {gerund} {path}: no rule allows it.",
            "notFound" => $"{principal} {verb} {path}: there's no such file.",
            "invalid" => $"{principal} {verb} {path}: refused (wrong size, or not in the right upload state).",
            _ => $"{principal} {verb} {path}: {outcome}.",
        };
    }

    private KeyValuePair<string, object?>[] Values =>
    [
        new("Operation", operation), new("Path", path), new("Principal", principal),
        new("Outcome", outcome), new("LatencyMs", latencyMs), new("{OriginalFormat}", "{Principal} {Operation} {Path}: {Outcome}"),
    ];

    public KeyValuePair<string, object?> this[int index] => Values[index];
    public int Count => 6;
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)Values).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
