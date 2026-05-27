using Winche.Storage.AspNetCore.Rest.DependencyInjection;
using Winche.Storage.DependencyInjection;
using Winche.Storage.S3.DependencyInjection;
using Winche.Storage.Sample.Configurations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWincheStorage(builder.Configuration, config =>
{
    config.AddFileAccessRule<AllowOwnerAccess>();
    config.AddFileStoreHook<FileUpdateHook>();
    config.AddS3Archive(builder.Configuration);
    config.SetCallerClaimsAccessor<UserClaimsMapper>();
});

var app = builder.Build();
app.UseWincheStorage();
app.UseWincheStorageRestApi();

await CascadeDeleteSmokeTest.RunAsync(app.Services);
await AccessRuleSmokeTest.RunAsync(app.Services);

app.Run();
