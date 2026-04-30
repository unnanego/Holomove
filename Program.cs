using Holomove;

Console.WriteLine();
Console.WriteLine("  ╔══════════════════════════════════════╗");
Console.WriteLine("  ║       Holomove - WP Migration        ║");
Console.WriteLine("  ╚══════════════════════════════════════╝");
Console.WriteLine();

var config = SiteConfig.Load();

if (!config.IsConfigured)
{
    Console.WriteLine("  No configuration found. Running setup...");
    config = SiteConfig.RunSetup();
}

var migrator = new WpMigrator(config);
await migrator.Init();

while (true)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Commands:");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    migrate  ");
    Console.ResetColor();
    Console.WriteLine("Sync source → backup → target (idempotent)");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    repair   ");
    Console.ResetColor();
    Console.WriteLine("Fix content URLs + missing featured images (fast)");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    relink   ");
    Console.ResetColor();
    Console.WriteLine("Fix internal post links with stale post-id suffixes");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    relink-test ");
    Console.ResetColor();
    Console.WriteLine("Dry-run relink on first 100 posts with hits (no push)");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    find-cyrillic ");
    Console.ResetColor();
    Console.WriteLine("List source posts with Cyrillic characters in slug");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    status   ");
    Console.ResetColor();
    Console.WriteLine("Compare source vs target, show progress");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    setup    ");
    Console.ResetColor();
    Console.WriteLine("Configure source/target WordPress sites");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    exit     ");
    Console.ResetColor();
    Console.WriteLine("Exit the application");
    Console.WriteLine();

    Console.Write("  > ");
    var input = Console.ReadLine()?.Trim().ToLowerInvariant();

    if (string.IsNullOrEmpty(input)) continue;

    if (input is "exit" or "quit" or "q")
    {
        Console.WriteLine("  Goodbye!");
        break;
    }

    try
    {
        switch (input)
        {
            case "migrate":
                await migrator.Migrate();
                break;
            case "repair":
                await migrator.Repair();
                break;
            case "relink":
                await migrator.Relink();
                break;
            case "relink-test":
                await migrator.Relink(dryRun: true, sampleLimit: 100);
                break;
            case "find-cyrillic":
                await migrator.FindCyrillicSlugs();
                break;
            case "status":
                await migrator.Status();
                break;
            case "setup":
                config = SiteConfig.RunSetup();
                migrator = new WpMigrator(config);
                await migrator.Init();
                break;
            default:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  Unknown command: {input}");
                Console.ResetColor();
                break;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  Error: {ex.Message}");
        Console.ResetColor();
    }
}
