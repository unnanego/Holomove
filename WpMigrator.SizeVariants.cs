using System.Text.RegularExpressions;

namespace Holomove;

public partial class WpMigrator
{
    /// <summary>
    /// Walks target post content for size-variant URLs (-WIDTHxHEIGHT) and rewrites
    /// each one to either (a) a closest-width variant that actually exists in the
    /// attachment's media_details.sizes, or (b) the base full-size URL when no
    /// suitable variant exists. Fixes broken images caused by theme not registering
    /// the size that was used during migration.
    /// </summary>
    public async Task FixSizeVariants()
    {
        await Authenticate();

        Console.WriteLine("\n  Fetching all target media (with sizes)...");
        var allMedia = await FetchAllPaginated<MediaItem>(
            _config.TargetWpApiUrl, "media", useAuth: true,
            extraQuery: "_fields=id,source_url,media_details");
        Console.WriteLine($"  {allMedia.Count} media item(s).");

        // Build a lookup: base filename → MediaItem (so we can find the parent for any variant URL).
        var byBaseFile = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in allMedia)
        {
            if (string.IsNullOrEmpty(m.SourceUrl)) continue;
            var fileName = Path.GetFileName(new Uri(m.SourceUrl).LocalPath);
            if (string.IsNullOrEmpty(fileName)) continue;
            byBaseFile.TryAdd(fileName, m);
        }

        Console.WriteLine("  Fetching target post content...");
        var contents = await FetchTargetContents();
        Console.WriteLine($"  Got content for {contents.Count} target post(s). Scanning for broken size variants...");

        var sizeVariantRegex = new Regex(
            @"https?://[^/""'\s<>]+/wp-content/uploads/[^""'\s<>]*?-(\d+)x(\d+)\.(jpe?g|png|gif|webp|avif)",
            RegexOptions.IgnoreCase);

        var rewriteMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unresolvable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetHost = _config.TargetDomain;

        // First pass: build URL → replacement map by scanning all post content
        foreach (var (_, value) in contents)
        {
            if (string.IsNullOrEmpty(value.Content)) continue;
            foreach (Match m in sizeVariantRegex.Matches(value.Content))
            {
                var url = m.Value;
                if (rewriteMap.ContainsKey(url) || unresolvable.Contains(url)) continue;

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { unresolvable.Add(url); continue; }
                var host = uri.Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
                if (!host.Equals(targetHost, StringComparison.OrdinalIgnoreCase)) { unresolvable.Add(url); continue; }

                if (!int.TryParse(m.Groups[1].Value, out var wantWidth)) { unresolvable.Add(url); continue; }

                var variantFileName = Path.GetFileName(uri.LocalPath);
                var baseMatch = Regex.Match(variantFileName, @"^(.+)-\d+x\d+(\.[^.]+)$");
                if (!baseMatch.Success) { unresolvable.Add(url); continue; }
                var baseFileName = baseMatch.Groups[1].Value + baseMatch.Groups[2].Value;

                if (!byBaseFile.TryGetValue(baseFileName, out var baseMedia))
                {
                    unresolvable.Add(url);
                    continue;
                }

                // Pick best replacement: closest-width existing variant whose width >= wantWidth,
                // else closest below, else base full-size source_url.
                var sizes = baseMedia.MediaDetails?.Sizes;
                string? replacement = null;

                if (sizes is { Count: > 0 })
                {
                    var available = sizes.Values
                        .Where(s => s.Width is > 0 && !string.IsNullOrEmpty(s.SourceUrl))
                        .OrderBy(s => s.Width)
                        .ToList();
                    if (available.Count > 0)
                    {
                        var atLeast = available.FirstOrDefault(s => s.Width >= wantWidth);
                        replacement = (atLeast ?? available.Last()).SourceUrl;
                    }
                }
                replacement ??= baseMedia.SourceUrl;

                if (string.IsNullOrEmpty(replacement) || replacement.Equals(url, StringComparison.OrdinalIgnoreCase))
                {
                    unresolvable.Add(url);
                    continue;
                }

                rewriteMap[url] = replacement;
            }
        }

        Console.WriteLine($"\n  Distinct broken size-variant URLs: {rewriteMap.Count + unresolvable.Count}");
        Console.WriteLine($"    resolvable (will rewrite):    {rewriteMap.Count}");
        Console.WriteLine($"    unresolvable (no base media): {unresolvable.Count}");

        if (rewriteMap.Count == 0)
        {
            Console.WriteLine("  Nothing to do.");
            return;
        }

        Console.WriteLine("\n  Sample rewrites (first 5):");
        foreach (var kv in rewriteMap.Take(5))
            Console.WriteLine($"    {kv.Key}\n      -> {kv.Value}");

        Console.Write("\n  Apply? [y/N]: ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (input != "y" && input != "yes")
        {
            Console.WriteLine("  Aborted.");
            return;
        }

        // Second pass: apply rewrites and push
        var updated = 0;
        var totalReplaced = 0;
        var done = 0;
        var total = contents.Count;
        var progress = new ProgressBar();

        foreach (var (postId, value) in contents)
        {
            var slug = _targetPosts.FirstOrDefault(p => p.Id == postId)?.Slug ?? postId.ToString();
            try
            {
                if (!string.IsNullOrEmpty(value.Content))
                {
                    var newContent = value.Content;
                    var localCount = 0;
                    foreach (var (oldUrl, newUrl) in rewriteMap)
                    {
                        if (newContent.Contains(oldUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            newContent = newContent.Replace(oldUrl, newUrl, StringComparison.OrdinalIgnoreCase);
                            localCount++;
                        }
                    }
                    if (localCount > 0)
                    {
                        await PostWithRetryAsync(
                            $"{_config.TargetWpApiUrl}wp/v2/posts/{postId}",
                            new { content = newContent });
                        updated++;
                        totalReplaced += localCount;
                        await Task.Delay(150);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n    Error rewriting {slug}: {ex.Message}");
            }
            done++;
            progress.Update(done, total, slug);
        }

        progress.Complete($"Rewrote {totalReplaced} variant URL(s) across {updated} post(s).");
    }
}
