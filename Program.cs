using Holomove;

Console.WriteLine();
Console.WriteLine("  ╔══════════════════════════════════════╗");
Console.WriteLine("  ║       Holomove - WP Migration        ║");
Console.WriteLine("  ╚══════════════════════════════════════╝");
Console.WriteLine();

var migrator = new WpMigrator();
await migrator.Init();

while (true)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Commands:");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    migrate        ");
    Console.ResetColor();
    Console.WriteLine("Sync and migrate posts from source to target");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    status         ");
    Console.ResetColor();
    Console.WriteLine("Compare source vs target, show migration progress");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    fix-duplicates ");
    Console.ResetColor();
    Console.WriteLine("Find and delete duplicate posts on target");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    fix-authors    ");
    Console.ResetColor();
    Console.WriteLine("Fix author mismatches on existing posts");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    fix-media      ");
    Console.ResetColor();
    Console.WriteLine("Re-upload missing media, fix broken URLs");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    fix-all        ");
    Console.ResetColor();
    Console.WriteLine("Run all fixes");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    exit           ");
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
            case "status":
                await migrator.Status();
                break;
            case "fix-duplicates":
                await migrator.FixDuplicates();
                break;
            case "fix-authors":
                await migrator.FixAuthors();
                break;
            case "fix-media":
                await migrator.FixMedia();
                break;
            case "fix-all":
                await migrator.FixAll();
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
