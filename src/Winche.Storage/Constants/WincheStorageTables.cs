namespace Winche.Storage.Constants;

/// <summary>Fixed object names. Multi-store deployments isolate via the connection's search_path schema.</summary>
public static class WincheStorageTables
{
    public const string Files = "winche_files";

    /// <summary>Pending (and dead-letter) hook deliveries, written in the same transaction as the file change.</summary>
    public const string HookDeliveries = "winche_storage_hook_deliveries";
}
