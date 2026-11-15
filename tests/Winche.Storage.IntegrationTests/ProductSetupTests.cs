using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Core;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Services;
using Xunit;

namespace Winche.Storage.IntegrationTests;

public class ProductSetupTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Set_up_on_demand_storage_makes_nothing_until_its_setup_runs()
    {
        var cs = fixture.DataSource.ConnectionString;
        if (!cs.Contains("Password", StringComparison.OrdinalIgnoreCase)) cs = new NpgsqlConnectionStringBuilder(cs) { Password = "postgres" }.ConnectionString;
        var name = "ondemand_" + Guid.NewGuid().ToString("n")[..10];
        await using (var create = fixture.DataSource.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
        cs = new NpgsqlConnectionStringBuilder(cs) { Database = name }.ConnectionString;

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWincheProductSetupOnDemand();
        builder.Services.AddWincheStorage(o => o.ConnectionString = cs);
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            async Task<bool> Has(string table)
            {
                await using var db = NpgsqlDataSource.Create(cs);
                await using var cmd = db.CreateCommand($"SELECT to_regclass('public.{table}') IS NOT NULL");
                return (bool)(await cmd.ExecuteScalarAsync())!;
            }
            await Task.Delay(300);
            Assert.False(await Has("winche_files"));

            var setup = host.Services.GetServices<IWincheProductSetup>().Single(s => s.Product == WincheProductNames.Storage);
            await setup.RunAllAsync();
            await setup.RunAllAsync();
            Assert.True(await Has("winche_files"));
            host.Services.GetRequiredService<WincheProductReadiness>().MarkReady(WincheProductNames.Storage);
            await host.Services.GetRequiredService<FileStorage>().SetAsync("a/b.txt", "text/plain", 3);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
