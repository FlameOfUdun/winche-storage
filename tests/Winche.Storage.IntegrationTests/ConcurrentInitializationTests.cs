using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Storage.DependencyInjection;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>Nodes starting together initialise the schema one at a time instead of racing the DDL.</summary>
public class ConcurrentInitializationTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Several_nodes_can_initialize_a_fresh_database_at_once()
    {
        var database = $"init_{Guid.NewGuid():N}";
        await using (var conn = await fx.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE DATABASE {database}";
            await cmd.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(fx.DataSource.ConnectionString) { Database = database }.ConnectionString;
        if (!connectionString.Contains("Password", StringComparison.OrdinalIgnoreCase))
            connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Password = "postgres" }.ConnectionString;

        var hosts = Enumerable.Range(0, 6).Select(_ =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWincheStorage(o => o.ConnectionString = connectionString);
            return builder.Build();
        }).ToList();

        await Task.WhenAll(hosts.Select(h => h.InitializeWincheStorageAsync()));

        await using var check = NpgsqlDataSource.Create(connectionString);
        await using var c = await check.OpenConnectionAsync();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT to_regclass('winche_files') IS NOT NULL";
        Assert.Equal(true, await q.ExecuteScalarAsync());

        foreach (var h in hosts) h.Dispose();
    }
}
