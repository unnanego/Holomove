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
            case "fix-galleries":
                await migrator.FixGalleries();
                break;
            case "test-gallery":
                await migrator.FixGalleries(testOne: true);
                break;
            case "unwrap-links":
                await migrator.UnwrapBrokenImageLinks();
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
            case "hit-post":
                if (string.IsNullOrEmpty(argTail))
                {
                    Console.WriteLine("  Usage: hit-post <slug>");
                    break;
                }
                await migrator.HitPost(argTail);
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
