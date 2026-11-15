using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Core;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Models;
using Winche.Storage.Services;
using Winche.Storage.Settings;
using Xunit;

namespace Winche.Storage.IntegrationTests;

/// <summary>o.UseSettings(): the size limit, request logging and orphan sweep changed from the console.</summary>
public class StorageSettingsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var cmd = fixture.DataSource.CreateCommand("DROP TABLE IF EXISTS winche_schema; DROP TABLE IF EXISTS winche_settings; DROP TABLE IF EXISTS winche_secrets;");
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private string ConnectionString
    {
        get
        {
            var cs = fixture.DataSource.ConnectionString;
            return cs.Contains("Password", StringComparison.OrdinalIgnoreCase) ? cs : new NpgsqlConnectionStringBuilder(cs) { Password = "postgres" }.ConnectionString;
        }
    }

    [Fact]
    public async Task The_size_limit_and_sweep_change_without_a_restart()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWincheConfig(o => o.ConnectionString = ConnectionString);
        builder.Services.AddWincheStorage(o =>
        {
            o.ConnectionString = ConnectionString;
            o.MaxFileSizeBytes = 2 * 1048576;
            o.UseOrphanSweep(s => s.Interval = TimeSpan.FromHours(12));
            o.UseSettings();
        });
        using var host = builder.Build();
        await host.InitializeWincheStorageAsync();
        await host.StartAsync();
        try
        {
            var area = host.Services.GetRequiredService<SettingsArea<StorageSettings>>();
            Assert.Equal((2.0, true, 12.0), (area.Current.MaxFileSizeMb, area.Current.OrphanSweep.Enabled, area.Current.OrphanSweep.IntervalHours));
            var storage = host.Services.GetRequiredService<FileStorage>();
            await storage.SetAsync("docs/a.bin", "application/octet-stream", 1048576);

            await host.Services.GetRequiredService<SettingsStore>().SaveAsync(StorageSettings.Area, JsonSerializer.SerializeToNode(area.Current with
            {
                MaxFileSizeMb = 0.5,
                OrphanSweep = area.Current.OrphanSweep with { Enabled = false, GraceHours = 48 },
            }, SettingsArea.Json)!, "admin@x", "Smaller uploads");

            var tooBig = await Assert.ThrowsAsync<FileTooLargeException>(() => storage.SetAsync("docs/b.bin", "application/octet-stream", 1048576));
            Assert.Equal(524288, tooBig.MaxBytes);
            var sweep = host.Services.GetRequiredService<IOptions<OrphanSweepOptions>>().Value;
            Assert.Equal((false, TimeSpan.FromHours(48)), (sweep.Enabled, sweep.GraceWindow));

            var bad = area.Current with { MaxFileSizeMb = 0, OrphanSweep = area.Current.OrphanSweep with { GraceHours = 0 } };
            var ex = await Assert.ThrowsAsync<SettingsValidationException>(() => host.Services.GetRequiredService<SettingsStore>()
                .SaveAsync(StorageSettings.Area, JsonSerializer.SerializeToNode(bad, SettingsArea.Json)!, null, "x"));
            Assert.Equal(2, ex.Errors.Count);
        }
        finally { await host.StopAsync(); }
    }
}
