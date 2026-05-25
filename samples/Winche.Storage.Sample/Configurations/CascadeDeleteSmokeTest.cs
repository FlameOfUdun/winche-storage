using Winche.Storage.Interfaces;

namespace Winche.Storage.Sample.Configurations;

public static class CascadeDeleteSmokeTest
{
    private const string Prefix = "cascade-smoke-test";

    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileManager>();

        Banner("CASCADE DELETE SMOKE TEST (STORAGE)");

        await CleanSlateAsync(files);
        await ScenarioSingleFile(files);

        await CleanSlateAsync(files);
        await ScenarioDirectoryCascade(files);

        await CleanSlateAsync(files);
        Banner("DONE");
    }

    private static async Task CleanSlateAsync(IFileManager files)
    {
        // Always-cascade DeleteUnprotectedAsync sweeps the test prefix clean.
        await files.DeleteUnprotectedAsync(Prefix);
    }

    private static async Task ScenarioSingleFile(IFileManager files)
    {
        Banner("Scenario 1: single-file delete (sanity check)");

        var path = $"{Prefix}/alone.png";
        await files.SetUnprotectedAsync(path, "image/png", 1024);

        Console.WriteLine("Seeded:");
        await PrintExistence(files, [path]);

        Console.WriteLine();
        Console.WriteLine($"Calling DeleteUnprotectedAsync(\"{path}\")...");
        var ok = await files.DeleteUnprotectedAsync(path);
        Console.WriteLine($"  returned: {ok}");
        Console.WriteLine();

        Console.WriteLine("After delete:");
        await PrintExistence(files, [path]);

        Assert($"{path} gone", await files.GetUnprotectedAsync(path) is null);
    }

    private static async Task ScenarioDirectoryCascade(IFileManager files)
    {
        Banner("Scenario 2: directory cascade (every file under a prefix)");

        var seeded = new[]
        {
            $"{Prefix}/userA/avatar.png",
            $"{Prefix}/userA/docs/resume.pdf",
            $"{Prefix}/userA/docs/cover.pdf",
            $"{Prefix}/userA/photos/2024/jan/01.jpg",
            $"{Prefix}/userB/avatar.png",
        };

        foreach (var p in seeded)
            await files.SetUnprotectedAsync(p, "application/octet-stream", 256);

        Console.WriteLine("Seeded:");
        await PrintExistence(files, seeded);

        var target = $"{Prefix}/userA";
        Console.WriteLine();
        Console.WriteLine($"Calling DeleteUnprotectedAsync(\"{target}\")  -- directory cascade");
        var ok = await files.DeleteUnprotectedAsync(target);
        Console.WriteLine($"  returned: {ok}");
        Console.WriteLine();

        Console.WriteLine("After delete:");
        await PrintExistence(files, seeded);

        Assert($"{Prefix}/userA/avatar.png gone",            await files.GetUnprotectedAsync($"{Prefix}/userA/avatar.png")            is null);
        Assert($"{Prefix}/userA/docs/resume.pdf gone",       await files.GetUnprotectedAsync($"{Prefix}/userA/docs/resume.pdf")       is null);
        Assert($"{Prefix}/userA/docs/cover.pdf gone",        await files.GetUnprotectedAsync($"{Prefix}/userA/docs/cover.pdf")        is null);
        Assert($"{Prefix}/userA/photos/2024/jan/01.jpg gone", await files.GetUnprotectedAsync($"{Prefix}/userA/photos/2024/jan/01.jpg") is null);
        Assert($"{Prefix}/userB/avatar.png preserved (sibling)", await files.GetUnprotectedAsync($"{Prefix}/userB/avatar.png")        is not null);
    }

    private static async Task PrintExistence(IFileManager files, IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var rec = await files.GetUnprotectedAsync(p);
            Console.WriteLine($"  {(rec is null ? "  -  " : "EXIST")}  {p}");
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
