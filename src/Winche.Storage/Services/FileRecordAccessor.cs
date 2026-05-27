using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.Models;
using Winche.Storage.Operations;

namespace Winche.Storage.Services;

public sealed class FileRecordAccessor(
    IOptions<StoreOptions> options,
    [FromKeyedServices(ServiceKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source
)
{
    private readonly string _table = options.Value.TableName;

    public async Task<FileRecord?> GetAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new GetFileOperation(conn, null, _table).ExecuteAsync(path, ct);
    }
}
