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

// Dispatch a single command. Returns false if unrecognized.
async Task<bool> RunCommand(string command, string argTail)
{
    switch (command)
    {
        case "migrate": await migrator.Migrate(); return true;
        case "audit": await migrator.RunAudit(); return true;
        case "fix-content": await migrator.FixContent(); return true;
        case "fix-galleries": await migrator.FixGalleries(); return true;
        case "test-gallery": await migrator.FixGalleries(testOne: true); return true;
        case "unwrap-links": await migrator.UnwrapBrokenImageLinks(); return true;
        case "relink": await migrator.Relink(); return true;
        case "dedupe-media": await migrator.DedupeMedia(); return true;
        case "finalize": await migrator.FinalizeUrls(); return true;
        case "status": await migrator.Status(); return true;
        case "hit-post":
            if (string.IsNullOrEmpty(argTail)) Console.WriteLine("  Usage: hit-post <slug>");
            else await migrator.HitPost(argTail);
            return true;
        case "setup":
            config = SiteConfig.RunSetup();
            migrator = new WpMigrator(config);
            await migrator.Init();
            return true;
        default: return false;
    }
}

// Headless mode: `Holomove <command> [args]` runs one command and exits (each command
// flushes its own caches on completion). Lets a long migrate run unattended / in background.
if (args.Length > 0)
{
    var headlessCmd = args[0].ToLowerInvariant();
    var headlessTail = args.Length > 1 ? string.Join(' ', args[1..]) : "";
    try
    {
        if (!await RunCommand(headlessCmd, headlessTail))
            Console.WriteLine($"  Unknown command: {headlessCmd}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  Error: {ex.Message}");
    }
    return;
}

while (true)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Commands:");
    PrintCommand("migrate",      "Sync source → backup → target (idempotent)");
    PrintCommand("audit",        "Diagnostics: cyrillic slugs + extra posts + broken media");
    PrintCommand("fix-content",  "Run all content fix passes (galleries, srcset, links, media, variants)");
    PrintCommand("fix-galleries","Replace broken Newspaper td-gallery sliders with WP gallery blocks");
    PrintCommand("test-gallery", "Run fix-galleries on the FIRST matching post only, with confirm");
    PrintCommand("unwrap-links", "Remove broken <a href> wrappers around images");
    PrintCommand("relink",       "Fix internal post links with stale post-id suffixes");
    PrintCommand("dedupe-media", "Merge duplicate media uploads");
    PrintCommand("finalize",     "Rewrite media URLs from target domain to canonical");
    PrintCommand("status",       "Compare source vs target, show progress");
    PrintCommand("hit-post",     "Time a noop update on one post (debug WP hangs): hit-post <slug>");
    PrintCommand("setup",        "Configure source/target WordPress sites");
    PrintCommand("exit",         "Exit the application");
    Console.WriteLine();

    Console.Write("  > ");
    var raw = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(raw)) continue;

    var spaceIdx = raw.IndexOf(' ');
    var input = (spaceIdx < 0 ? raw : raw[..spaceIdx]).ToLowerInvariant();
    var argTail = spaceIdx < 0 ? "" : raw[(spaceIdx + 1)..].Trim();

    if (input is "exit" or "quit" or "q")
    {
        Console.WriteLine("  Goodbye!");
        break;
    }

    try
    {
        if (!await RunCommand(input, argTail))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Unknown command: {input}");
            Console.ResetColor();
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  Error: {ex.Message}");
        Console.ResetColor();
    }
}
