using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Core;
using Winche.Storage.Constants;
using Winche.Storage.Interfaces;

namespace Winche.Storage.Services;

internal sealed class SchemaManager(
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

            -- Durable hook deliveries (8.3.0): one row per hook per file change, deleted once the hook succeeds.
            CREATE TABLE IF NOT EXISTS {{WincheStorageTables.HookDeliveries}} (
                id              BIGSERIAL   PRIMARY KEY,
                hook            TEXT        NOT NULL,
                kind            TEXT        NOT NULL,
                path            TEXT        NOT NULL,
                record          JSONB       NULL,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
                next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                attempts        INT         NOT NULL DEFAULT 0,
                last_error      TEXT        NULL,
                dead_at         TIMESTAMPTZ NULL
            );

            CREATE INDEX IF NOT EXISTS idx_{{WincheStorageTables.HookDeliveries}}_due
                ON {{WincheStorageTables.HookDeliveries}}(next_attempt_at) WHERE dead_at IS NULL;

            CREATE INDEX IF NOT EXISTS idx_{{WincheStorageTables.HookDeliveries}}_order
                ON {{WincheStorageTables.HookDeliveries}}(hook, path, id) WHERE dead_at IS NULL;
            """;

        // Numbered steps under one lock (Winche.Core): new tables or columns go in a new step.
        // Step 2: deletes match a file and everything under it (path LIKE 'a/b/%'), which the primary key can't serve
        // unless the database's collation is C.
        const string prefixIndex = $"CREATE INDEX IF NOT EXISTS idx_{WincheStorageTables.Files}_path_prefix ON {WincheStorageTables.Files} (path text_pattern_ops)";
        // Step 3: each upload has an archive key of its own (ObjectKeys), so bytes are never overwritten in place.
        // Files from before keep theirs, which was their path.
        const string objectKeys = $"""
            ALTER TABLE {WincheStorageTables.Files} ADD COLUMN IF NOT EXISTS object_key text, ADD COLUMN IF NOT EXISTS upload_key text;
            UPDATE {WincheStorageTables.Files} SET object_key = path WHERE upload_status = 1 AND object_key IS NULL;
            UPDATE {WincheStorageTables.Files} SET upload_key = path WHERE upload_status <> 1 AND upload_key IS NULL;
            CREATE INDEX IF NOT EXISTS idx_{WincheStorageTables.Files}_object_key ON {WincheStorageTables.Files} (object_key);
            CREATE INDEX IF NOT EXISTS idx_{WincheStorageTables.Files}_upload_key ON {WincheStorageTables.Files} (upload_key);
            """;
        // Step 4: download headers a file can set (Firebase's contentDisposition and cacheControl).
        const string headers = $"ALTER TABLE {WincheStorageTables.Files} ADD COLUMN IF NOT EXISTS content_disposition text, ADD COLUMN IF NOT EXISTS cache_control text";
        await WincheSchema.EnsureAsync(source, "storage", [sql, prefixIndex, objectKeys, headers], ct);
    }
}
