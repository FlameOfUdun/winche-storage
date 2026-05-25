using System.Text.Json;
using Winche.Storage.Abstraction;
using Winche.Storage.Models;
using WincheSentinel.Models;

namespace Winche.Storage.Sample.Configurations;

internal class AllowOwnerAccess : FileAccessRule
{
    public override string Path => "userFiles/{userId}/**";

    public override IReadOnlySet<AccessOperation> Operations => new HashSet<AccessOperation> { AccessOperation.Read, AccessOperation.Write, AccessOperation.Delete };

    public override async Task<bool> EvaluateAsync(AccessContext<FileRecord> context, CancellationToken ct)
    {
        Console.WriteLine(JsonSerializer.Serialize(context.Claims));
        Console.WriteLine(JsonSerializer.Serialize(context.Params));

        var claimUserId = context.Claims.GetValueOrDefault("userId")?.ToString();
        var pathUserId = context.Params.GetValueOrDefault("userId")?.ToString();
        Console.WriteLine(string.Equals(claimUserId, pathUserId));
        return claimUserId == pathUserId;
    }
}