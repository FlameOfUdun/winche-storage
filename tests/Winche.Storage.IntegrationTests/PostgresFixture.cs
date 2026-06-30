using Npgsql;
using Testcontainers.PostgreSql;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>
/// Spins up a throwaway Postgres container once per test class and applies the Winche schema.
/// Requires a running Docker daemon.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        // SchemaManager's [FromKeyedServices] attribute is a DI hint only; direct construction is fine.
        await new SchemaManager(DataSource).EnsureCreatedAsync();
    }

    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE winche_files";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}
