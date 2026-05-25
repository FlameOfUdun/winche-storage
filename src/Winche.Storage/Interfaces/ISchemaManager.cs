namespace Winche.Storage.Interfaces;

internal interface ISchemaManager
{
    Task EnsureCreatedAsync(CancellationToken ct = default);
}
