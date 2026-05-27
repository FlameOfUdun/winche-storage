# Winche.Storage

Lightweight .NET libraries for storing file metadata in PostgreSQL and objects in S3-compatible archives via presigned URLs. The solution includes core abstractions, an S3 archive provider, a minimal REST adapter for ASP.NET Core, and a sample app.

Integrates with [Winche.Sentinel](https://github.com/FlameOfUdun/winche-sentinel) for access control.

## Packages

| Package | Description |
| --- | --- |
| `Winche.Storage` | Core: schema management, file CRUD, hooks, access rules |
| `Winche.Storage.S3` | S3-compatible archive provider (presigned URLs, multipart upload) |
| `Winche.Storage.AspNetCore.Rest` | Minimal API REST endpoints + claims accessor base |

## Install

```cmd
dotnet add package Winche.Storage
dotnet add package Winche.Storage.S3
dotnet add package Winche.Storage.AspNetCore.Rest
```

## Quick Start

### 1. Configure `appsettings.json`

```json
{
  "ConnectionStrings": {
    "WincheStorage": "Host=localhost;Database=mydb;Username=user;Password=pass"
  },
  "WincheStorage": {
    "Schema": "public",
    "TableName": "files"
  },
  "WincheStorage:S3Archive": {
    "BucketName": "your-bucket",
    "RegionName": "us-east-1",
    "AccessKey": "YOUR_ACCESS_KEY",
    "SecretKey": "YOUR_SECRET_KEY",
    "PresignedUrlExpiry": "00:15:00"
  }
}
```

`AccessKey` and `SecretKey` are optional. Omit them when deploying to AWS with a task role or instance profile — the SDK uses ambient IAM credentials automatically.

The connection string is read from `WincheStorage`, falling back to `DefaultConnection`.

### 2. Register services

```csharp
builder.Services.AddWincheStorage(builder.Configuration, config =>
{
    config.AddS3Archive(builder.Configuration);
});

builder.Services.AddWincheStorageRestApi();
```

### 3. Initialize schema and map endpoints

```csharp
app.UseWincheStorage();           // creates the files table if it doesn't exist
app.UseWincheStorageRestApi();    // maps REST routes under "files/" prefix
```

See [samples/Winche.Storage.Sample](samples/Winche.Storage.Sample) for a complete working example.

## Configuration

### `AddWincheStorage` overloads

```csharp
// Read connection string and options from IConfiguration
services.AddWincheStorage(configuration, configure);

// Provide connection string and options explicitly
services.AddWincheStorage(
    options => { options.Schema = "storage"; options.TableName = "files"; },
    connectionString: "...",
    configure);
```

### `StoreOptions`

| Property | Default | Description |
| --- | --- | --- |
| `Schema` | `"public"` | PostgreSQL schema for the metadata table |
| `TableName` | `"files"` | Table name for file records |
| `EnsureCreated` | `true` | Auto-create schema/table on startup |

### `S3ArchiveOptions`

| Property | Default | Description |
| --- | --- | --- |
| `BucketName` | _(required)_ | S3 bucket name |
| `RegionName` | _(required)_ | AWS region (e.g. `"us-east-1"`) |
| `AccessKey` | `null` | Optional — omit to use ambient credentials |
| `SecretKey` | `null` | Optional — omit to use ambient credentials |
| `PresignedUrlExpiry` | `00:15:00` | Lifetime of generated presigned URLs |

You can also configure S3 with a delegate instead of `IConfiguration`:

```csharp
config.AddS3Archive(opts =>
{
    opts.BucketName = "my-bucket";
    opts.RegionName = "eu-west-1";
});
```

## REST Endpoints

All `{path}` segments are **base64-encoded** file paths or directory paths.

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/{path}` | Register a new file record |
| `GET` | `/{path}` | Get a file record |
| `DELETE` | `/{path}` | Delete a file record and its archive object |
| `PUT` | `/{path}/metadata` | Replace file metadata |
| `POST` | `/{path}/confirm` | Confirm a direct upload is complete |
| `GET` | `/{path}/upload` | Generate a presigned upload URL |
| `GET` | `/{path}/download` | Generate a presigned download URL |
| `GET` | `/{path}/list` | List files in a directory (`?mimeType=` filter optional) |
| `GET` | `/{path}/upload/parts/{partNumber}` | Sign a multipart upload part |
| `GET` | `/{path}/parts` | List uploaded parts for a multipart upload |

### Custom prefix and middleware

```csharp
app.UseWincheStorageRestApi(
    prefix: "storage",
    configure: group => group.RequireAuthorization());
```

## Access Control

Implement `FileAccessRule` to guard file operations. Rules are evaluated by Winche.Sentinel for every protected `IFileManager` call.

```csharp
public class AllowOwnerAccess : FileAccessRule
{
    public override string Path => "userFiles/{userId}/**";

    public override IReadOnlySet<AccessOperation> Operations =>
        new HashSet<AccessOperation> { AccessOperation.Read, AccessOperation.Write, AccessOperation.Delete };

    public override Task<bool> EvaluateAsync(AccessContext<FileRecord> context, CancellationToken ct)
    {
        var claimUserId = context.Claims.GetValueOrDefault("userId")?.ToString();
        var pathUserId  = context.Params.GetValueOrDefault("userId")?.ToString();
        return Task.FromResult(claimUserId == pathUserId);
    }
}
```

- `Path` is a glob pattern; named segments like `{userId}` are captured into `context.Params`.
- `context.Claims` contains the values returned by your `FileClaimsAccessor`.
- Register with `config.AddFileAccessRule<AllowOwnerAccess>()`.

### Claims Accessor (REST)

Map HTTP request data to claims that access rules can read:

```csharp
public class UserClaimsMapper : FileClaimsAccessor
{
    public override IReadOnlyDictionary<string, object?> MapClaims(HttpContext httpContext) =>
        new Dictionary<string, object?>
        {
            ["userId"] = httpContext.Request.Headers.TryGetValue("X-USER-ID", out var id)
                ? id.ToString() : ""
        };
}
```

Register: `config.SetCallerClaimsAccessor<UserClaimsMapper>()`.

## Hooks

Implement `FileStoreHook` to react to file lifecycle events. Hooks are matched by `Path` (same glob syntax as access rules) and dispatched asynchronously.

```csharp
public class AuditHook : FileStoreHook
{
    public override string Path => "userFiles/{userId}/**";

    public override Task OnFileRegisteredAsync(FileRecord record, CancellationToken ct) { ... }
    public override Task OnUploadConfirmedAsync(FileRecord record, CancellationToken ct) { ... }
    public override Task OnFileDeletedAsync(string path, CancellationToken ct) { ... }
    public override Task OnMetadataUpdatedAsync(FileRecord record, CancellationToken ct) { ... }
    public override Task OnUploadUrlGeneratedAsync(string path, UploadSession session, CancellationToken ct) { ... }
    public override Task OnDownloadUrlGeneratedAsync(string path, DownloadSession session, CancellationToken ct) { ... }
}
```

Register: `config.AddFileStoreHook<AuditHook>()`.

## `IFileManager`

Inject `IFileManager` directly to interact with the store without going through the REST layer.

```csharp
// Protected variants — access rules are enforced
Task<FileRecord>               SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata, CancellationToken ct);
Task<FileRecord?>              GetAsync(string path, CancellationToken ct);
Task<FileRecord?>              UpdateMetadataAsync(string path, JsonObject patch, CancellationToken ct);
Task<bool>                     DeleteAsync(string path, CancellationToken ct);
Task<UploadSession>            GenerateUploadUrlAsync(string path, CancellationToken ct);
Task<DownloadSession>          GenerateDownloadUrlAsync(string path, CancellationToken ct);
Task<FileRecord>               ConfirmUploadAsync(string path, CancellationToken ct);
Task<IEnumerable<FileRecord>>  ListAsync(string directory, string? mimeType, CancellationToken ct);
Task<UploadSession>            SignPartAsync(string path, int partNumber, CancellationToken ct);
Task<IEnumerable<FilePart>>    ListUploadedPartsAsync(string path, CancellationToken ct);

// Unprotected variants — bypass access rules (for server-side / trusted callers)
Task<FileRecord>               SetUnprotectedAsync(...)
Task<FileRecord?>              GetUnprotectedAsync(...)
// ... same surface, Unprotected suffix
```

### `FileRecord`

| Field | Type | Description |
| --- | --- | --- |
| `id` | `string` | Unique record identifier |
| `path` | `string` | Full logical path |
| `directory` | `string` | Parent directory segment |
| `mimeType` | `string` | MIME type |
| `sizeBytes` | `long` | Declared file size |
| `uploadStatus` | `UploadStatus` | `pending`, `complete`, or `failed` |
| `uploadId` | `string?` | Multipart upload ID (when active) |
| `metadata` | `JsonObject` | Arbitrary key/value metadata |
| `version` | `long` | Optimistic-concurrency version counter |
| `createdAt` | `DateTime` | Creation timestamp |
| `updatedAt` | `DateTime` | Last-modified timestamp |

## Requirements

- .NET 10 SDK (`net10.0`)
- PostgreSQL for metadata storage
- An S3-compatible bucket for object storage (AWS S3, MinIO, etc.)

## License

Elastic License 2.0
