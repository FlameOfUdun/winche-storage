using Microsoft.Extensions.Logging;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Text;
using Winche.Storage.Authorization;
using Winche.Storage.Logging;
using Xunit;
using Winche.Values;

namespace Winche.Storage.Tests;

public class RequestLogTests
{
    private sealed class Capture : ILogger
    {
        public List<(LogLevel Level, string Message, Dictionary<string, object?> Values)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception),
                state is IEnumerable<KeyValuePair<string, object?>> kv ? kv.ToDictionary(x => x.Key, x => x.Value) : []));
    }

    private static (RuleGuardedFileStorage Storage, Capture Log) Guarded(double rate, string? uid)
    {
        var log = new Capture();
        var claims = () => uid is null ? null : (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["uid"] = uid };
        var engine = new RuleEngine(new StaticRuleSetRepository(RuleText.ParseRuleSet(
            "match uploads/{uid}/{file=**} { allow read, delete: if request.auth.uid == uid; }")));
        var storage = new RuleGuardedFileStorage(new RecordingFileStorage { DeleteReturns = true }, engine, claims,
            new RequestLog(log, new RequestLogOptions { SuccessSampleRate = rate }, claims));
        return (storage, log);
    }

    [Fact]
    public async Task Requests_are_logged_with_who_what_and_outcome()
    {
        var (storage, log) = Guarded(1.0, "u1");
        await storage.DeleteAsync("uploads/u1/a.png");
        await storage.GetAsync("uploads/u1/missing.png");   // the recording store returns null: not found
        await Assert.ThrowsAsync<AccessDeniedException>(() => storage.DeleteAsync("uploads/u2/b.png"));

        Assert.Equal(3, log.Entries.Count);
        Assert.StartsWith("u1 deleted uploads/u1/a.png (", log.Entries[0].Message);
        Assert.Equal("storage.delete", log.Entries[0].Values["Operation"]);
        Assert.Equal("notFound", log.Entries[1].Values["Outcome"]);
        Assert.Equal("u1 read uploads/u1/missing.png: there's no such file.", log.Entries[1].Message);
        Assert.Equal(LogLevel.Warning, log.Entries[2].Level);
        Assert.Equal("u1 was denied deleting uploads/u2/b.png: no rule allows it.", log.Entries[2].Message);
    }

    [Fact]
    public async Task Successes_are_sampled_denials_never()
    {
        var (storage, log) = Guarded(0.0, null);
        await Assert.ThrowsAsync<AccessDeniedException>(() => storage.DeleteAsync("uploads/u2/b.png"));
        var entry = Assert.Single(log.Entries);
        Assert.Equal("signed out", entry.Values["Principal"]);
    }
}
