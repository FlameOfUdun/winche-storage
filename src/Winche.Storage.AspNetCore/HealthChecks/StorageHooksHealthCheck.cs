using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Winche.Storage.Services;

namespace Winche.Storage.AspNetCore.HealthChecks;

/// <summary>
/// Degraded when file hooks fall behind (the oldest change a hook hasn't taken is older than <c>maxLag</c>) or some gave up
/// (dead letters); the counts are in the result's data.
/// </summary>
public sealed class StorageHooksHealthCheck(StorageHooks hooks, TimeSpan maxLag) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        StorageHookStatus status;
        try { status = await hooks.StatusAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded("Could not read the file hooks' status", ex);
        }

        var data = new Dictionary<string, object>
        {
            ["failing"] = status.Failing, ["deadLetters"] = status.DeadLetters,
        };
        var problems = new List<string>();
        if (status.OldestPending is { } oldest && DateTimeOffset.UtcNow - oldest > maxLag)
            problems.Add($"a hook still has a change from {oldest:u} to take");
        if (status.DeadLetters > 0) problems.Add($"{status.DeadLetters} dead letter(s)");
        return problems.Count == 0 ? HealthCheckResult.Healthy(data: data) : HealthCheckResult.Degraded(string.Join("; ", problems), data: data);
    }
}

public static class StorageHooksHealthCheckExtensions
{
    /// <summary>Adds <see cref="StorageHooksHealthCheck"/> (degraded past <paramref name="maxLag"/>, default 5 minutes).</summary>
    public static IHealthChecksBuilder AddWincheStorageHooks(
        this IHealthChecksBuilder builder, string name = "winche-storage-hooks", TimeSpan? maxLag = null, IEnumerable<string>? tags = null) =>
        builder.Add(new HealthCheckRegistration(
            name,
            sp => new StorageHooksHealthCheck(sp.GetRequiredService<StorageHooks>(), maxLag ?? TimeSpan.FromMinutes(5)),
            HealthStatus.Degraded,
            tags));
}
