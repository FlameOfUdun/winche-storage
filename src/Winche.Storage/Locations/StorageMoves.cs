using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Winche.Core;
using Winche.Storage.Constants;
using Winche.Core;
using Winche.Storage.Interfaces;

namespace Winche.Storage.Locations;

/// <summary>How a location test went, step by step.</summary>
public sealed record LocationTest(bool Ok, IReadOnlyList<LocationTestStep> Steps);
public sealed record LocationTestStep(string Name, bool Ok, string? Error = null);

/// <summary>A move's progress: written by the server doing the copying, read by the console.</summary>
public sealed record MoveProgress(string MoveId, int Total, int Copied, int AlreadyThere, int Failed, long Bytes, string? LastError,
    DateTimeOffset? PassStartedAt, DateTimeOffset? PassFinishedAt, int Passes);

/// <summary>Problems a move step can't go ahead with, in a sentence.</summary>
public sealed class StorageMoveException(string message) : Exception(message);

/// <summary>
/// Moving files to another location, as guided steps: <see cref="StartAsync"/> (copying while the old location stays in
/// use), <see cref="SwitchAsync"/> once everything is copied, <see cref="FinishAsync"/> once nothing reads the old one.
/// </summary>
public sealed class StorageMoves(
    SettingsArea<StorageLocationSettings> area, ArchiveLocations locations, IServiceProvider services,
    [FromKeyedServices(WincheStorageKeys.DATA_SOURCE_KEY)] NpgsqlDataSource db)
{
    private const string Winche = "Winche";
    public StorageLocationSettings State => area.Current;

    private SettingsStore Store => services.GetService<SettingsStore>()
        ?? throw new StorageMoveException("Moving files needs AddWincheConfig(…): that's where the location and the move are kept.");

    /// <summary>Writes, reads back and deletes a small file; checks upload links can be made.</summary>
    public async Task<LocationTest> TestAsync(ArchiveLocation location, string? secretKey, CancellationToken ct = default)
    {
        var steps = new List<LocationTestStep>();
        if (location.Problems() is { Count: > 0 } problems) return new(false, [new("Settings", false, string.Join(" ", problems))]);
        IArchive archive;
        try { archive = secretKey is null ? await locations.GetAsync(location, ct) : locations.Build(location, secretKey); }
        catch (Exception ex) { return new(false, [new("Connect", false, ex.Message)]); }
        if (archive is not IArchiveContent content) return new(false, [new("Copy files", false, "Files can't be copied into this kind of location.")]);

        var probe = $"winche-location-test/{Guid.NewGuid():N}.txt";
        var text = $"Winche location test {DateTimeOffset.UtcNow:O}";
        async Task<bool> Step(string name, Func<Task> body)
        {
            try { await body(); steps.Add(new(name, true)); return true; }
            catch (Exception ex) when (!ct.IsCancellationRequested) { steps.Add(new(name, false, Short(ex))); return false; }
        }
        var ok = await Step("Write a file", () => content.WriteAsync(probe, new MemoryStream(Encoding.UTF8.GetBytes(text)), "text/plain", ct))
            && await Step("Read it back", async () =>
            {
                await using var s = await content.OpenReadAsync(probe, ct) ?? throw new InvalidOperationException("The file wasn't there.");
                using var reader = new StreamReader(s);
                if (await reader.ReadToEndAsync(ct) != text) throw new InvalidOperationException("It came back different.");
            })
            && await Step("Make upload and download links", async () =>
            {
                await archive.GenerateUploadUrlAsync(probe, "text/plain", text.Length, ct);
                await archive.GenerateDownloadUrlAsync(probe, ct);
            });
        var deleted = await Step("Delete it", () => archive.DeleteAsync(probe, ct));
        return new(ok && deleted, steps);
    }

    private static string Short(Exception ex) => ex.GetBaseException().Message is { Length: > 300 } m ? m[..300] : ex.GetBaseException().Message;

    /// <summary>Starts copying every file to <paramref name="to"/>; the app keeps using the current location meanwhile.</summary>
    public async Task<StorageMove> StartAsync(ArchiveLocation to, string? secretKey, string? by, string reason, CancellationToken ct = default)
    {
        if (State.Move is not null) throw new StorageMoveException("A move is already under way: finish or cancel it first.");
        if (to.Problems() is { Count: > 0 } problems) throw new StorageMoveException(string.Join(" ", problems));
        if ((to.Kind == "code" && State.Active is null) || (State.Active is { } a && a.Kind == to.Kind && JsonSerializer.Serialize(a with { Id = "" }) == JsonSerializer.Serialize(to with { Id = "" })))
            throw new StorageMoveException("Files are already kept there.");
        if (await locations.GetAsync(State.Active, ct) is not IArchiveContent)
            throw new StorageMoveException("Files can't be copied out of the current location (its archive can't be read from the server).");
        if (!string.IsNullOrEmpty(secretKey))
        {
            var secrets = services.GetService<SecretStore>() is { Available: true } s ? s
                : throw new StorageMoveException("Secrets need the project's master key: set WincheConfigOptions.MasterKey.");
            await secrets.SetAsync(to.SecretName, secretKey, by, ct);
        }
        var move = new StorageMove { Id = Guid.NewGuid().ToString("N")[..12], Phase = StorageMove.Copying, From = State.Active, To = to, StartedAt = DateTimeOffset.UtcNow };
        await SaveAsync(State with { Move = move }, by, reason, ct);
        return move;
    }

    /// <summary>Points the app at the new location (once everything is copied). Reads fall back to the old one until <see cref="FinishAsync"/>.</summary>
    public async Task SwitchAsync(string? by, string reason, CancellationToken ct = default)
    {
        if (State.Move is not { } m) throw new StorageMoveException("No move is under way.");
        if (m.Phase != StorageMove.Ready) throw new StorageMoveException(m.Phase == StorageMove.Switched ? "Already switched." : "Wait until every file is copied.");
        await SaveAsync(new StorageLocationSettings { Active = m.To!.Kind == "code" ? null : m.To, Move = m with { Phase = StorageMove.Switched } }, by, reason, ct);
    }

    /// <summary>Ends the move: the old location is no longer read or written. Its files stay there to delete by hand.</summary>
    public async Task FinishAsync(string? by, string reason, CancellationToken ct = default)
    {
        if (State.Move is not { Phase: StorageMove.Switched }) throw new StorageMoveException("Switch to the new location first.");
        var progress = await ProgressAsync(ct);
        if (progress is not { PassFinishedAt: not null, Failed: 0 } || progress.PassFinishedAt < State.Move.StartedAt)
            throw new StorageMoveException("Wait for a catch-up pass with no failures after the switch.");
        await SaveAsync(State with { Move = null }, by, reason, ct);
    }

    /// <summary>Stops a move before the switch; files already copied stay in the new location.</summary>
    public async Task CancelAsync(string? by, string reason, CancellationToken ct = default)
    {
        if (State.Move is not { } m) throw new StorageMoveException("No move is under way.");
        if (m.Phase == StorageMove.Switched)
            throw new StorageMoveException("The app already uses the new location: finish this move, then move back if you need to.");
        await SaveAsync(State with { Move = null }, by, reason, ct);
    }

    internal async Task SaveAsync(StorageLocationSettings next, string? by, string reason, CancellationToken ct) =>
        await Store.SaveAsync(StorageLocationSettings.Area, JsonSerializer.SerializeToNode(next, SettingsArea.Json)!, by, reason, ct);

    internal async Task MarkReadyAsync(string moveId, CancellationToken ct)
    {
        if (State.Move is { Phase: StorageMove.Copying } m && m.Id == moveId)
            await SaveAsync(State with { Move = m with { Phase = StorageMove.Ready } }, Winche, "Every file is copied: ready to switch", ct);
    }

    // ── progress ────────────────────────────────────────────────────────────────

    private bool _tableReady;

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_tableReady) return;
        await using var cmd = db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS winche_storage_moves (
              move_id text PRIMARY KEY, total int NOT NULL DEFAULT 0, copied int NOT NULL DEFAULT 0, already_there int NOT NULL DEFAULT 0,
              failed int NOT NULL DEFAULT 0, bytes bigint NOT NULL DEFAULT 0, last_error text NULL, pass_started timestamptz NULL,
              pass_finished timestamptz NULL, passes int NOT NULL DEFAULT 0);
            """);
        await cmd.ExecuteNonQueryAsync(ct);
        _tableReady = true;
    }

    public async Task<MoveProgress?> ProgressAsync(CancellationToken ct = default)
    {
        if (State.Move is not { } m) return null;
        await EnsureTableAsync(ct);
        await using var cmd = db.CreateCommand("SELECT total, copied, already_there, failed, bytes, last_error, pass_started, pass_finished, passes FROM winche_storage_moves WHERE move_id = $1");
        cmd.Parameters.AddWithValue(m.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new MoveProgress(m.Id, 0, 0, 0, 0, 0, null, null, null, 0);
        return new MoveProgress(m.Id, r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.GetInt32(8));
    }

    internal async Task WriteProgressAsync(MoveProgress p, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        await using var cmd = db.CreateCommand("""
            INSERT INTO winche_storage_moves (move_id, total, copied, already_there, failed, bytes, last_error, pass_started, pass_finished, passes)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT (move_id) DO UPDATE SET total = EXCLUDED.total, copied = EXCLUDED.copied, already_there = EXCLUDED.already_there,
              failed = EXCLUDED.failed, bytes = EXCLUDED.bytes, last_error = EXCLUDED.last_error, pass_started = EXCLUDED.pass_started,
              pass_finished = EXCLUDED.pass_finished, passes = EXCLUDED.passes
            """);
        cmd.Parameters.AddWithValue(p.MoveId);
        cmd.Parameters.AddWithValue(p.Total);
        cmd.Parameters.AddWithValue(p.Copied);
        cmd.Parameters.AddWithValue(p.AlreadyThere);
        cmd.Parameters.AddWithValue(p.Failed);
        cmd.Parameters.AddWithValue(p.Bytes);
        cmd.Parameters.AddWithValue((object?)p.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)p.PassStartedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)p.PassFinishedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue(p.Passes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal async Task<string?> ContentTypeAsync(string path, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT mime_type FROM {WincheStorageTables.Files} WHERE path = $1 LIMIT 1");
        cmd.Parameters.AddWithValue(path);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    internal NpgsqlDataSource Db => db;
}

/// <summary>
/// Does the copying for a move, on one server at a time: a full pass while copying (then marks it ready), and catch-up
/// passes after the switch for files that reached the old location meanwhile.
/// </summary>
internal sealed class StorageMoveWorker(StorageMoves moves, ArchiveLocations locations, ILogger<StorageMoveWorker> logger) : BackgroundService
{
    internal static TimeSpan Tick = TimeSpan.FromSeconds(2);
    internal static TimeSpan CatchUpEvery = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastPass = DateTimeOffset.MinValue;
    private string? _lastMove;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (moves.State.Move is not { } move) continue;
            if (move.Phase == StorageMove.Ready) continue;
            if (move.Id == _lastMove && move.Phase == StorageMove.Switched && DateTimeOffset.UtcNow - _lastPass < CatchUpEvery) continue;
            try
            {
                await using var lease = await AdvisoryLease.TryAcquireAsync(moves.Db, "storage-move", TimeSpan.FromSeconds(30), stoppingToken);
                if (lease is null) continue;   // another server is copying
                using var active = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Lost);
                var failed = await PassAsync(move, active.Token);
                _lastPass = DateTimeOffset.UtcNow;
                _lastMove = move.Id;
                if (failed == 0 && move.Phase == StorageMove.Copying) await moves.MarkReadyAsync(move.Id, active.Token);
                else if (failed > 0) await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Moving files failed; trying again shortly.");
                await moves.WriteProgressAsync(new MoveProgress(move.Id, 0, 0, 0, 1, 0, ex.GetBaseException().Message, DateTimeOffset.UtcNow, null, 0), CancellationToken.None);
            }
        }
    }

    private async Task<int> PassAsync(StorageMove move, CancellationToken ct)
    {
        var source = await locations.GetAsync(move.From, ct);
        var target = await locations.GetAsync(move.To, ct);
        if (source is not IArchiveContent from || target is not IArchiveContent to)
            throw new StorageMoveException("Files can't be copied between these locations.");
        var previous = await moves.ProgressAsync(ct);
        var p = new MoveProgress(move.Id, 0, 0, 0, 0, 0, null, DateTimeOffset.UtcNow, null, (previous?.Passes ?? 0) + 1);
        int total = 0, copied = 0, there = 0, failed = 0;
        long bytes = 0;
        string? lastError = null;
        await foreach (var obj in source.ListObjectsAsync(null, ct))
        {
            total++;
            try
            {
                if (await target.HeadAsync(obj.Key, ct) is not null) there++;
                else
                {
                    await using var stream = await from.OpenReadAsync(obj.Key, ct);
                    if (stream is null) { total--; continue; }   // deleted meanwhile
                    var counting = new CountingStream(stream);
                    await to.WriteAsync(obj.Key, counting, await moves.ContentTypeAsync(obj.Key, ct), ct);
                    bytes += counting.Count;
                    copied++;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                lastError = $"{obj.Key}: {ex.GetBaseException().Message}";
            }
            if (total % 50 == 0) await moves.WriteProgressAsync(p with { Total = total, Copied = copied, AlreadyThere = there, Failed = failed, Bytes = bytes, LastError = lastError }, ct);
        }
        await moves.WriteProgressAsync(p with
        {
            Total = total, Copied = copied, AlreadyThere = there, Failed = failed, Bytes = bytes, LastError = lastError, PassFinishedAt = DateTimeOffset.UtcNow,
        }, ct);
        if (copied > 0 || failed > 0) logger.LogInformation("Move {Move}: copied {Copied} of {Total} files ({Failed} failed).", move.Id, copied, total, failed);
        return failed;
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); Count += n; return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = await inner.ReadAsync(buffer, ct); Count += n; return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
