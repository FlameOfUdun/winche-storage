using Microsoft.Extensions.DependencyInjection;
using Winche.Core;
using Winche.Storage.Interfaces;

namespace Winche.Storage.Services;

/// <summary>Storage's setup, step by step (what <c>InitializeWincheStorageAsync</c> does), for hosts that set products up on demand.</summary>
internal sealed class StorageProductSetup(IServiceProvider services) : IWincheProductSetup
{
    public string Product => WincheProductNames.Storage;

    public IReadOnlyList<string> Steps { get; } = ["Create the file tables", "Start keeping changes for hooks"];

    public async Task RunStepAsync(int step, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        switch (step)
        {
            case 0:
                await scope.ServiceProvider.GetRequiredService<ISchemaManager>().EnsureCreatedAsync(ct);
                // Made on first use otherwise: the progress of moves between disk and buckets.
                if (scope.ServiceProvider.GetService<Locations.StorageMoves>() is { } moves) await moves.EnsureTableAsync(ct);
                break;
            case 1: await scope.ServiceProvider.GetRequiredService<StorageHooks>().TrackAsync(ct); break;
            default: throw new ArgumentOutOfRangeException(nameof(step));
        }
    }
}
