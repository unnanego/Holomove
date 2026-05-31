namespace Holomove;

public partial class WpMigrator
{
    /// <summary>
    /// Runs every read-only audit in sequence: Cyrillic-slug source posts,
    /// extra target posts (no source counterpart), broken target media URLs.
    /// </summary>
    public async Task RunAudit()
    {
        Console.WriteLine("\n  ── Audit 1/3: Cyrillic slugs ──");
        await FindCyrillicSlugs();

        Console.WriteLine("\n  ── Audit 2/3: Extra target posts ──");
        await FindExtraTargets();

        Console.WriteLine("\n  ── Audit 3/3: Broken target media ──");
        await FindBrokenMedia();
    }

    /// <summary>
    /// Runs all target-content fix-up passes in dependency order:
    ///   1. fix-galleries: replaces broken Newspaper td-gallery sliders with WP
    ///      gallery blocks (and drops their orphaned style block).
    ///   2. clean-srcset: strips srcset/sizes so target WP rebuilds responsive
    ///      attrs on render. Eliminates most broken source-host srcset entries
    ///      in a single shot.
    ///   3. unwrap-links: removes broken &lt;a&gt; wrappers around &lt;img&gt;
    ///      pointing at dead wp-content files (source-host or target-not-in-lib).
    ///   4. fix-broken-media: uploads remaining stale source URLs from backup
    ///      and rewrites them, plus uploads any target-host img refs not in lib.
    ///   5. fix-size-variants: rewrites any leftover broken -WxH URLs to existing
    ///      variants or base full-size.
    /// Each pass prompts y/N where applicable.
    /// </summary>
    public async Task FixContent()
    {
        Console.WriteLine("\n  ── Fix 1/5: Replace broken Newspaper galleries ──");
        await FixGalleries();

        Console.WriteLine("\n  ── Fix 2/5: Strip srcset/sizes ──");
        await CleanSrcset();

        Console.WriteLine("\n  ── Fix 3/5: Unwrap broken image links ──");
        await UnwrapBrokenImageLinks();

        Console.WriteLine("\n  ── Fix 4/5: Repair broken media URLs ──");
        await FixBrokenMedia();

        Console.WriteLine("\n  ── Fix 5/5: Rewrite broken size variants ──");
        await FixSizeVariants();
    }
}
