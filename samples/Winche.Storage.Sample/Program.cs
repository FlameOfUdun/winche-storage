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

    opts.UseRules(rb =>
    {
        rb.Match("userFiles/{userId}", mb =>
        {
            mb.Allow(RuleOperations.All, Expr.Auth("uid").Eq(Expr.Param("userId")));
        });

        rb.Match("userFiles/{userId}/{rest=**}", mb =>
        {
            mb.Allow(RuleOperations.All, Expr.Auth("uid").Eq(Expr.Param("userId")));
        });
    });

    opts.UseHooks(h =>
    {
        h.Add<FileUpdateHook>("userFiles/{userId}/{file=**}");
    });

    opts.UseS3Archive(s3 =>
    {
        builder.Configuration.GetSection("WincheStorage:S3Archive").Bind(s3);
    });
    
    opts.MapClaims(ctx =>
    {
        // The SDK sends `Authorization: Bearer <token>`. This sample takes the
        // token verbatim as the uid, so signing in as "alice" scopes to
        // userFiles/alice and the rules below actually separate users. A real
        // deployment validates a JWT here and reads its subject.
        //
        // Previously this returned a fixed "user-123" for every request, which
        // made the sample unable to demonstrate multi-user isolation at all.
        var header = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header)) return null; // -> rules deny

        const string prefix = "Bearer ";
        var token = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..]
            : header;

        token = token.Trim();
        return string.IsNullOrWhiteSpace(token)
            ? null
            : new Dictionary<string, object?> { ["uid"] = token };
    });
});

var app = builder.Build();
await app.InitializeWincheStorageAsync();
app.MapWincheStorageRestApi();

app.Run();
app.WaitForShutdown();
