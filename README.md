# Winche.Storage

Lightweight .NET libraries and sample apps for storing file metadata in PostgreSQL and objects in S3-compatible archives (presigned URLs). The solution includes core abstractions, a Postgres+S3 file-store implementation, a small REST adapter, and a sample ASP.NET app.

Integrates with [Winche.Sentinel](https://github.com/FlameOfUdun/winche-sentinel) for access control.

## Install

```cmd
dotnet add package Winche.Storage
```

Add the REST API integrations as needed:

```cmd
dotnet add package Winche.Storage.AspNetCore.Rest
```

## Quick Start

### 1. Configure `appsettings.json`

```json
{
  "WincheStorage": {
    "S3Archive": {
      "BucketName": "your-bucket",
      "AccessKey": "YOUR_ACCESS_KEY",
      "SecretKey": "YOUR_SECRET_KEY",
      "RegionName": "us-east-1",
      "PresignedUrlExpiry": "00:15:00"
    }
  }
}
```

### 2. Register services

```csharp
builder.Services.AddWincheStorage(connectionString, builder.Configuration);

// Add REST API (optional)
builder.Services.AddWincheStorageRestApi();
```

### 3. Initialize schema and map endpoints

```csharp
// Ensures the file records table
app.UseWincheStorage();

// Map REST routes (default prefix: "files")
app.UseWincheStorageRestApi();
```

See [samples/WebApi](samples/Winche.Storage.Sample) for a complete working example.

## Packages

| Package | Description |
| --- | --- |
| `Winche.Storage` | Core file store: schema, CRUD, queries, file management |
| `Winche.Storage.AspNetCore.Rest` | ASP.NET Core REST endpoints |

## Requirements

- .NET 10 SDK (see `<TargetFramework>` in each project; this repo targets `net10.0`)
- PostgreSQL for metadata storage
- An S3-compatible bucket for object storage (or mock for local development)

## License

Elastic License 2.0
