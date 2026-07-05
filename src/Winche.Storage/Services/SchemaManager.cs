using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Storage.Constants;
using Winche.Storage.Interfaces;

namespace Winche.Storage.Services;

public sealed class SchemaManager(
    [FromKeyedServices(WincheStorageKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source
) : ISchemaManager
{
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        var sql = $$"""
            CREATE TABLE IF NOT EXISTS {{WincheStorageTables.Files}} (
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
                upload_id     TEXT       NULL,
                content_hash  TEXT       NULL
            );

            ALTER TABLE {{WincheStorageTables.Files}} ADD COLUMN IF NOT EXISTS content_hash TEXT;

            CREATE INDEX IF NOT EXISTS idx_{{WincheStorageTables.Files}}_directory
                ON {{WincheStorageTables.Files}}(directory);

            CREATE INDEX IF NOT EXISTS idx_{{WincheStorageTables.Files}}_id
                ON {{WincheStorageTables.Files}}(id);

            CREATE INDEX IF NOT EXISTS idx_{{WincheStorageTables.Files}}_directory_id
                ON {{WincheStorageTables.Files}}(directory, id ASC);

            CREATE INDEX IF NOT EXISTS idx_{{WincheStorageTables.Files}}_metadata
                ON {{WincheStorageTables.Files}} USING GIN(metadata);
            """;

        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
