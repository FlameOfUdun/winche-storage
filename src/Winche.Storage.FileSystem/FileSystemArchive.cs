using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Storage.FileSystem;

/// <summary>
/// Winche.Storage's bytes in a folder. Clients upload and download straight to and from your app through URLs this
/// archive signs (HMAC-SHA256, expiring, bound to the method, the path and — for uploads — the size), the way they
/// would with S3's presigned URLs. Map the endpoints with <c>app.MapWincheStorageFileSystem()</c>.
/// </summary>
public sealed class FileSystemArchive : IArchive, Winche.Storage.Locations.IArchiveContent
{
    private readonly FileSystemArchiveOptions _options;
    private readonly IHttpContextAccessor _http;
    private readonly TimeProvider _time;
    private readonly byte[] _key;

    public FileSystemArchive(IOptions<FileSystemArchiveOptions> options, IHttpContextAccessor http, TimeProvider time,
        Winche.Core.WincheKeys? masterKeys = null)
    {
        _options = options.Value;
        // No key of its own: the one Winche.Core derives from the project's master key (as Winche Server always used).
        if (string.IsNullOrEmpty(_options.SigningKey) && masterKeys?.TryDerive("file-links") is { } derived) _options.SigningKey = derived;
        _options.Validate();
        _http = http;
        _time = time;
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(_options.SigningKey));
        Root = Directory.CreateDirectory(_options.Root).FullName;
        Directory.CreateDirectory(ObjectsDir);
        Directory.CreateDirectory(PartsDir);
    }

    /// <summary>The folder, made absolute.</summary>
    public string Root { get; }

    internal FileSystemArchiveOptions Options => _options;
    private string ObjectsDir => Path.Combine(Root, "objects");
    private string PartsDir => Path.Combine(Root, "parts");

    // ── IArchiveContent (moving files between locations) ──────────────────────────

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var file = FileOf(path);
        return Task.FromResult<Stream?>(File.Exists(file) ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null);
    }

    public async Task WriteAsync(string path, Stream content, string? contentType, CancellationToken ct = default)
    {
        var file = FileOf(path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = TempBeside(file);
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(output, ct);
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ── IArchive ────────────────────────────────────────────────────────────────

    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default)
    {
        FileOf(path);   // a bad path fails here, not at upload
        var expires = Expires();
        var max = Math.Max(0, sizeBytes);
        var sig = Sign("PUT", path, expires, max);
        return Task.FromResult(new UploadSession { Url = $"{Base()}/o/{Escape(path)}?exp={expires}&max={max}&sig={sig}", ExpiresAt = At(expires) });
    }

    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default)
    {
        FileOf(path);
        var expires = Expires();
        var sig = Sign("GET", path, expires, 0);
        return Task.FromResult(new DownloadSession { Url = $"{Base()}/o/{Escape(path)}?exp={expires}&sig={sig}", ExpiresAt = At(expires) });
    }

    public async Task<ArchiveObjectInfo?> HeadAsync(string path, CancellationToken ct = default)
    {
        var file = FileOf(path);
        return File.Exists(file) ? new ArchiveObjectInfo(new FileInfo(file).Length, await ETagAsync(file, ct)) : null;
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var file = FileOf(path);
        if (File.Exists(file)) File.Delete(file);
        PruneEmptyFolders(Path.GetDirectoryName(file)!);
        return Task.CompletedTask;
    }

    public async Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        foreach (var p in paths) await DeleteAsync(p, ct);
    }

    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var file in Directory.EnumerateFiles(ObjectsDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(file).StartsWith(".winche-", StringComparison.Ordinal)) continue;   // an upload in progress
            var key = Path.GetRelativePath(ObjectsDir, file).Replace('\\', '/');
            if (prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                yield return new ArchivedObject(key, File.GetLastWriteTimeUtc(file));
        }
    }

    public Task<string> CreateMultipartUploadAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default)
    {
        FileOf(path);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Directory.CreateDirectory(PartsOf(id));
        File.WriteAllText(Path.Combine(PartsOf(id), "path"), path);
        File.WriteAllText(Path.Combine(PartsOf(id), "size"), sizeBytes.ToString(CultureInfo.InvariantCulture));
        return Task.FromResult(id);
    }

    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default)
    {
        // No part can hold more than the whole file, so a part link never lets a client store more than it declared.
        var dir = UploadOf(path, uploadId);
        var declared = long.Parse(File.ReadAllText(Path.Combine(dir, "size")), CultureInfo.InvariantCulture);
        var max = Math.Max(1, Math.Min(_options.MaxPartBytes, declared));
        var expires = Expires();
        var sig = Sign("PART", $"{uploadId}/{partNumber}", expires, max);
        return Task.FromResult(new UploadSession
        {
            Url = $"{Base()}/p/{uploadId}/{partNumber}?exp={expires}&max={max}&sig={sig}", ExpiresAt = At(expires),
        });
    }

    /// <summary>The parts folder of a multipart upload of <paramref name="path"/>; throws when there's no such upload.</summary>
    private string UploadOf(string path, string uploadId)
    {
        var dir = PartsOf(uploadId);
        if (!Directory.Exists(dir) || File.ReadAllText(Path.Combine(dir, "path")) != path) throw StorageException.UploadGone(path);
        return dir;
    }

    public async Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        var dir = UploadOf(path, uploadId);
        var file = FileOf(path);
        var temp = TempBeside(file);
        await using (var output = File.Create(temp))
            foreach (var part in Parts(dir))
                await using (var input = File.OpenRead(Path.Combine(dir, part.Number.ToString(CultureInfo.InvariantCulture))))
                    await input.CopyToAsync(output, ct);
        File.Move(temp, file, overwrite: true);
        Directory.Delete(dir, recursive: true);
        return await ETagAsync(file, ct);
    }

    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<FilePart>>(Parts(UploadOf(path, uploadId)));

    public Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        if (Directory.Exists(PartsOf(uploadId))) Directory.Delete(PartsOf(uploadId), recursive: true);
        return Task.CompletedTask;
    }

    // ── the endpoints' side ─────────────────────────────────────────────────────

    /// <summary>Whether a request's <c>exp</c>/<c>max</c>/<c>sig</c> match what this archive signed for it.</summary>
    internal bool Verify(string method, string subject, string? exp, string? max, string? sig, out long maxBytes)
    {
        maxBytes = 0;
        if (!long.TryParse(exp, NumberStyles.None, CultureInfo.InvariantCulture, out var expires)) return false;
        if (max is not null && !long.TryParse(max, NumberStyles.None, CultureInfo.InvariantCulture, out maxBytes)) return false;
        if (expires < _time.GetUtcNow().ToUnixTimeSeconds() || sig is null) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(method, subject, expires, maxBytes));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(sig));
    }

    /// <summary>The file for an object path, refusing anything that would leave the folder.</summary>
    internal string FileOf(string path)
    {
        var clean = (path ?? "").Replace('\\', '/').Trim('/');
        if (clean.Length == 0 || clean.Split('/').Any(s => s is "" or "." or ".." || s.StartsWith(".winche-", StringComparison.Ordinal) || s.Contains(':')))
            throw new ArgumentException($"'{path}' isn't a valid object path.", nameof(path));
        var full = Path.GetFullPath(Path.Combine(ObjectsDir, clean));
        if (!full.StartsWith(ObjectsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"'{path}' isn't a valid object path.", nameof(path));
        return full;
    }

    internal string PartsOf(string uploadId)
    {
        if (uploadId.Length != 32 || !uploadId.All(char.IsAsciiHexDigitLower)) throw new ArgumentException("Bad upload id.", nameof(uploadId));
        return Path.Combine(PartsDir, uploadId);
    }

    internal static string TempBeside(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return Path.Combine(Path.GetDirectoryName(file)!, $".winche-{Guid.NewGuid():N}.tmp");
    }

    internal static async Task<string> ETagAsync(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        return $"\"{Convert.ToHexStringLower(await MD5.HashDataAsync(stream, ct))}\"";
    }

    private void PruneEmptyFolders(string dir)
    {
        while (dir.StartsWith(ObjectsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir)!;
        }
    }

    private static List<FilePart> Parts(string dir) =>
        [.. Directory.EnumerateFiles(dir)
            .Select(f => (Name: Path.GetFileName(f), Size: new FileInfo(f).Length))
            .Where(f => int.TryParse(f.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(f => new FilePart { Number = int.Parse(f.Name, CultureInfo.InvariantCulture), Size = f.Size })
            .OrderBy(p => p.Number)];

    private string Sign(string method, string subject, long expires, long max) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{method}\n{subject}\n{expires}\n{max}")));

    private long Expires() => (_time.GetUtcNow() + _options.UrlLifetime).ToUnixTimeSeconds();

    private static DateTime At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

    private static string Escape(string path) => string.Join('/', path.Replace('\\', '/').Trim('/').Split('/').Select(Uri.EscapeDataString));

    private string Base()
    {
        var root = _options.PublicBaseUrl?.TrimEnd('/');
        if (root is null)
        {
            var request = _http.HttpContext?.Request
                ?? throw new InvalidOperationException("FileSystemArchive: outside a request, set PublicBaseUrl so URLs know the app's address.");
            root = $"{request.Scheme}://{request.Host}{request.PathBase}";
        }
        return root + _options.RoutePrefix;
    }
}
