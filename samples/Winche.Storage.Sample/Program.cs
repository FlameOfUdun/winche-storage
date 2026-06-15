using Winche.Rules;
using Winche.Rules.Expressions;
using Winche.Storage.AspNetCore.DependencyInjection;
using Winche.Storage.AspNetCore.Rest.DependencyInjection;
using Winche.Storage.DependencyInjection;
using Winche.Storage.S3.DependencyInjection;
using Winche.Storage.Sample.Configurations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWincheStorage(opts =>
{
    opts.ConnectionString =
        builder.Configuration.GetConnectionString("WincheStorage") ??
        builder.Configuration.GetConnectionString("DefaultConnection") ??
        throw new InvalidOperationException("No connection string found for WincheStorage.");

    opts.UseRules(r => r.Match("userFiles/{userId}/{rest=**}", owned =>
        owned.Allow(RuleOperations.All, Expr.Auth("token", "userId").Eq(Expr.Param("userId")))));

    opts.AddFileStoreHook<FileUpdateHook>();
    opts.AddS3Archive(builder.Configuration);
    opts.MapClaims(ctx => new Dictionary<string, object?>
    {
        ["userId"] = "user-123",
    });
});

var app = builder.Build();
await app.InitializeWincheStorageAsync();
app.MapWincheStorageRestApi();

app.Run();
app.WaitForShutdown();
