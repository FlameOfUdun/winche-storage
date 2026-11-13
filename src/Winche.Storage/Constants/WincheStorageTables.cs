namespace Winche.Storage.Constants;

/// <summary>Fixed object names. Multi-store deployments isolate via the connection's search_path schema.</summary>
public static class WincheStorageTables
{
    public const string Files = "winche_files";

    /// <summary>The per-hook outbox before hooks consumed <see cref="Feed"/>; dropped by the schema (step 5).</summary>
    public const string HookDeliveries = "winche_storage_hook_deliveries";

    /// <summary>Storage's change feed (Winche.Core's event feed): one row per file change, for hooks.</summary>
    public const string Feed = "winche_files_feed";
}
