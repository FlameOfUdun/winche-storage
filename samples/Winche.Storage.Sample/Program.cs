using Winche.Storage.AspNetCore.Rest.DependencyInjection;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Sample.Configurations;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddWincheStorage(connectionString, builder.Configuration, config =>
{
    config.AddFileAccessRule<AllowOwnerAccess>();
    config.AddFileStoreHook<FileUpdateHook>();
});
builder.Services.AddWincheStorageRestApi(config =>
{
    config.AddClaimsMapper<UserClaimsMapper>();
});

var app = builder.Build();
app.UseWincheStorage();
app.UseWincheStorageRestApi();

// await CascadeDeleteSmokeTest.RunAsync(app.Services);

app.Run();
