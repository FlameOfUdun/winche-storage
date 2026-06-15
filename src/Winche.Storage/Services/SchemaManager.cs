using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;

namespace Winche.Storage.Services;

public sealed class SchemaManager(
    [FromKeyedServices(ServiceKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source, 
    IOptions<WincheStorageOptions> options
) : ISchemaManager
{
    private readonly string tableName = options.Value.TableName;

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        var sql = $$"""
            CREATE TABLE IF NOT EXISTS {{tableName}} (
                id          TEXT        NOT NULL,
                directory   TEXT        NOT NULL,
                path        TEXT        PRIMARY KEY,
                metadata    JSONB       NOT NULL DEFAULT '{}',
                created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version     BIGINT      NOT NULL DEFAULT 1,
                mime_type   TEXT        NOT NULL,
                size_bytes  BIGINT      NOT NULL,
                upload_status SMALLINT  NOT NULL,
                upload_id     TEXT       NULL
            );
            
            CREATE INDEX IF NOT EXISTS idx_{{tableName}}_directory
                ON {{tableName}}(directory);
            
            CREATE INDEX IF NOT EXISTS idx_{{tableName}}_id
                ON {{tableName}}(id);
            
            CREATE INDEX IF NOT EXISTS idx_{{tableName}}_directory_id
                ON {{tableName}}(directory, id ASC);
            
            CREATE INDEX IF NOT EXISTS idx_{{tableName}}_metadata
                ON {{tableName}} USING GIN(metadata);
            """;

        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}