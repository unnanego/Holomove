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

void PrintCommand(string name, string desc)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"    {name,-14}");
    Console.ResetColor();
    Console.WriteLine(desc);
}

while (true)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Commands:");
    PrintCommand("migrate",      "Sync source → backup → target (idempotent)");
    PrintCommand("audit",        "Diagnostics: cyrillic slugs + extra posts + broken media");
    PrintCommand("fix-content",  "Strip srcset, repair broken URLs, rewrite size variants");
    PrintCommand("relink",       "Fix internal post links with stale post-id suffixes");
    PrintCommand("dedupe-media", "Merge duplicate media uploads");
    PrintCommand("finalize",     "Rewrite media URLs from target domain to canonical");
    PrintCommand("status",       "Compare source vs target, show progress");
    PrintCommand("setup",        "Configure source/target WordPress sites");
    PrintCommand("exit",         "Exit the application");
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
            case "audit":
                await migrator.RunAudit();
                break;
            case "fix-content":
                await migrator.FixContent();
                break;
            case "relink":
                await migrator.Relink();
                break;
            case "dedupe-media":
                await migrator.DedupeMedia();
                break;
            case "finalize":
                await migrator.FinalizeUrls();
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
