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
    Console.Write("    migrate  ");
    Console.ResetColor();
    Console.WriteLine("Sync source → backup → target (idempotent)");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("    status   ");
    Console.ResetColor();
    Console.WriteLine("Compare source vs target, show progress");
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
            case "status":
                await migrator.Status();
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
