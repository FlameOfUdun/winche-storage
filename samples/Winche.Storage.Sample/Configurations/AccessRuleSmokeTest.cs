using Winche.Sentinel.Models;
using Winche.Storage.AspNetCore.Rest.Abstraction;
using Winche.Storage.Interfaces;

namespace Winche.Storage.Sample.Configurations;

public static class AccessRuleSmokeTest
{
    private const string Prefix = "userFiles";

    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileManager>();
        var claimsAccessor = scope.ServiceProvider.GetRequiredService<FileClaimsAccessor>();

        Banner("ACCESS RULE SMOKE TESTS (STORAGE)");

        await CleanSlateAsync(files);
        await ScenarioOwnerAccessGranted(files, claimsAccessor);

        await CleanSlateAsync(files);
        await ScenarioCrossUserAccessDenied(files, claimsAccessor);

        await CleanSlateAsync(files);
        await ScenarioNoMatchingRuleDenies(files, claimsAccessor);

        await CleanSlateAsync(files);
        await ScenarioUnprotectedBypassesRules(files);

        await CleanSlateAsync(files);
        Banner("DONE");
    }

    // -------------------------------------------------------------------------
    // Scenarios
    // -------------------------------------------------------------------------

    private static async Task ScenarioOwnerAccessGranted(IFileManager files, FileClaimsAccessor claimsAccessor)
    {
        Banner("Scenario 1: owner can write, read, and delete their own files");

        claimsAccessor.SetClaims(new Dictionary<string, object?> { ["userId"] = "alice" });
        Console.WriteLine("Caller: userId = \"alice\"");

        var path = $"{Prefix}/alice/avatar.png";

        var record = await files.SetAsync(path, "image/png", 1024);
        Console.WriteLine($"  SetAsync(\"{path}\") -> {record.Path}");
        Assert("SetAsync succeeded for owner", record.Path == path);

        var fetched = await files.GetAsync(path);
        Console.WriteLine($"  GetAsync(\"{path}\") -> {(fetched is null ? "null" : fetched.Path)}");
        Assert("GetAsync returned file for owner", fetched is not null);

        var listed = (await files.ListAsync($"{Prefix}/alice")).ToList();
        Console.WriteLine($"  ListAsync(\"{Prefix}/alice\") -> {listed.Count} file(s)");
        Assert("ListAsync returned file for owner", listed.Count == 1);

        var deleted = await files.DeleteAsync(path);
        Console.WriteLine($"  DeleteAsync(\"{path}\") -> {deleted}");
        Assert("DeleteAsync succeeded for owner", deleted);

        claimsAccessor.SetClaims(new Dictionary<string, object?>());
    }

    private static async Task ScenarioCrossUserAccessDenied(IFileManager files, FileClaimsAccessor claimsAccessor)
    {
        Banner("Scenario 2: cross-user access is denied");

        await files.SetUnprotectedAsync($"{Prefix}/alice/secret.png", "image/png", 512);

        claimsAccessor.SetClaims(new Dictionary<string, object?> { ["userId"] = "bob" });
        Console.WriteLine("Caller: userId = \"bob\"  (file belongs to alice)");

        var getBlocked = await ThrowsAsync<AccessDeniedException>(
            () => files.GetAsync($"{Prefix}/alice/secret.png"));
        Console.WriteLine($"  GetAsync(\"alice/secret.png\") threw AccessDeniedException: {getBlocked}");
        Assert("GetAsync denied for non-owner", getBlocked);

        var writeBlocked = await ThrowsAsync<AccessDeniedException>(
            () => files.SetAsync($"{Prefix}/alice/injected.png", "image/png", 256));
        Console.WriteLine($"  SetAsync(\"alice/injected.png\") threw AccessDeniedException: {writeBlocked}");
        Assert("SetAsync denied for non-owner path", writeBlocked);

        var deleteBlocked = await ThrowsAsync<AccessDeniedException>(
            () => files.DeleteAsync($"{Prefix}/alice/secret.png"));
        Console.WriteLine($"  DeleteAsync(\"alice/secret.png\") threw AccessDeniedException: {deleteBlocked}");
        Assert("DeleteAsync denied for non-owner", deleteBlocked);

        claimsAccessor.SetClaims(new Dictionary<string, object?>());
    }

    private static async Task ScenarioNoMatchingRuleDenies(IFileManager files, FileClaimsAccessor claimsAccessor)
    {
        Banner("Scenario 3: path outside any rule throws NoRulesMatchedException");

        await files.SetUnprotectedAsync("public/announcement.txt", "text/plain", 64);

        claimsAccessor.SetClaims(new Dictionary<string, object?> { ["userId"] = "alice" });
        Console.WriteLine("Caller: userId = \"alice\"  (path: public/announcement.txt — no rule matches)");

        var blocked = await ThrowsAsync<NoRulesMatchedException>(
            () => files.GetAsync("public/announcement.txt"));
        Console.WriteLine($"  GetAsync(\"public/announcement.txt\") threw NoRulesMatchedException: {blocked}");
        Assert("operation on unruled path throws NoRulesMatchedException", blocked);

        claimsAccessor.SetClaims(new Dictionary<string, object?>());
    }

    private static async Task ScenarioUnprotectedBypassesRules(IFileManager files)
    {
        Banner("Scenario 4: unprotected methods bypass access rules");

        Console.WriteLine("Caller: no claims set");

        var path = $"{Prefix}/alice/direct.png";

        var record = await files.SetUnprotectedAsync(path, "image/png", 128);
        Console.WriteLine($"  SetUnprotectedAsync(\"{path}\") -> {record.Path}");
        Assert("SetUnprotectedAsync succeeded with no claims", record.Path == path);

        var fetched = await files.GetUnprotectedAsync(path);
        Console.WriteLine($"  GetUnprotectedAsync(\"{path}\") -> {(fetched is null ? "null" : fetched.Path)}");
        Assert("GetUnprotectedAsync returned file with no claims", fetched is not null);

        var deleted = await files.DeleteUnprotectedAsync(path);
        Console.WriteLine($"  DeleteUnprotectedAsync(\"{path}\") -> {deleted}");
        Assert("DeleteUnprotectedAsync succeeded with no claims", deleted);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task CleanSlateAsync(IFileManager files)
    {
        await files.DeleteUnprotectedAsync(Prefix);
        await files.DeleteUnprotectedAsync("public");
    }

    private static async Task<bool> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static void Assert(string label, bool ok)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
        Console.ForegroundColor = prev;
    }

    private static void Banner(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', Math.Max(40, title.Length + 4)));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', Math.Max(40, title.Length + 4)));
    }
}
