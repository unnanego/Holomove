namespace Holomove;

public partial class WpMigrator
{
    /// <summary>
    /// Finds duplicate media library items uploaded by repeated migration runs
    /// (WP appends -1, -2 on filename collision). Groups by year/month/base
    /// filename (with size-variant and collision-suffix stripped), keeps the
    /// lowest ID, rewrites post content URLs to the canonical, deletes the rest.
    /// </summary>
    public async Task DedupeMedia()
    {
        await Authenticate();

        Console.WriteLine("\n  Fetching all target media (with metadata)...");
        var allMedia = await FetchAllPaginated<MediaItem>(
            _config.TargetWpApiUrl, "media", useAuth: true,
            extraQuery: "_fields=id,source_url,post,media_details");
        Console.WriteLine($"  {allMedia.Count} media item(s).");

        // Group by content-identity only: filesize + dimensions. Cross-post dupes
        // happen when ghost runs created multiple posts that each got their own
        // upload of the same source file. Unattached dupes happen when ghost posts
        // were later deleted but their attachments remained.
        var groups = new Dictionary<string, List<MediaItem>>(StringComparer.OrdinalIgnoreCase);
        var nullMediaDetails = 0;
        var nullFileSize = 0;
        var nullDims = 0;
        var groupedByContent = 0;
        var groupedByFilename = 0;
        var skippedNoKey = 0;

        foreach (var m in allMedia)
        {
            if (string.IsNullOrEmpty(m.SourceUrl)) continue;

            // Track why each item ended up where it did
            if (m.MediaDetails == null) nullMediaDetails++;
            else
            {
                if (!(m.MediaDetails.FileSize is > 0)) nullFileSize++;
                if (!(m.MediaDetails.Width is > 0 && m.MediaDetails.Height is > 0)) nullDims++;
            }

            var key = ComputeMediaDedupeKey(m);
            if (key == null) { skippedNoKey++; continue; }
            if (key.StartsWith("size=")) groupedByContent++;
            else if (key.StartsWith("name=")) groupedByFilename++;

            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(m);
        }

        Console.WriteLine($"\n  Diagnostics:");
        Console.WriteLine($"    items with no media_details:   {nullMediaDetails}");
        Console.WriteLine($"    items with no filesize:        {nullFileSize}");
        Console.WriteLine($"    items with no dimensions:      {nullDims}");
        Console.WriteLine($"    grouped by filesize+dim:       {groupedByContent}");
        Console.WriteLine($"    grouped by filename (fallback): {groupedByFilename}");
        Console.WriteLine($"    skipped (no usable key):       {skippedNoKey}");
        Console.WriteLine($"    distinct group keys:           {groups.Count}");

        var dupeGroups = groups
            .Where(kv => kv.Value.Count > 1)
            .OrderByDescending(kv => kv.Value.Count)
            .ToList();
        if (dupeGroups.Count == 0)
        {
            Console.WriteLine("  No duplicates found.");
            return;
        }

        var totalDupes = dupeGroups.Sum(g => g.Value.Count - 1);
        Console.WriteLine($"\n  Found {dupeGroups.Count} group(s) with duplicates — {totalDupes} extra item(s).");

        // Classify dupes by attachment pattern to help judge safety.
        var samePostGroups = dupeGroups.Count(g => g.Value.Select(i => i.Post).Distinct().Count() == 1 && g.Value.First().Post != 0);
        var crossPostGroups = dupeGroups.Count(g => g.Value.Select(i => i.Post).Where(p => p != 0).Distinct().Count() > 1);
        var anyUnattachedGroups = dupeGroups.Count(g => g.Value.Any(i => i.Post == 0));
        Console.WriteLine($"    same-post groups:    {samePostGroups}");
        Console.WriteLine($"    cross-post groups:   {crossPostGroups}");
        Console.WriteLine($"    contain unattached:  {anyUnattachedGroups}");

        Console.WriteLine("\n  Sample (top 8 groups by dupe count):");
        foreach (var kv in dupeGroups.Take(8))
        {
            Console.WriteLine($"    [{kv.Value.Count}] {kv.Key}");
            foreach (var item in kv.Value.OrderBy(i => i.Id))
                Console.WriteLine($"        id={item.Id}  post={item.Post}  {item.SourceUrl}");
        }

        Console.Write("\n  Apply (rewrite post content + delete dupes)? [y/N]: ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (input != "y" && input != "yes")
        {
            Console.WriteLine("  Aborted.");
            return;
        }

        // Build URL → canonical URL map. Lowest ID = canonical (oldest).
        var urlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toDelete = new List<int>();
        foreach (var (_, group) in dupeGroups)
        {
            var sorted = group.OrderBy(i => i.Id).ToList();
            var canonical = sorted[0];
            for (var i = 1; i < sorted.Count; i++)
            {
                urlMap[sorted[i].SourceUrl] = canonical.SourceUrl;
                toDelete.Add(sorted[i].Id);
            }
        }

        // Rewrite post content
        Console.WriteLine("\n  Fetching target post content...");
        var contents = await FetchTargetContents();
        Console.WriteLine($"  Got content for {contents.Count} target post(s). Rewriting URLs...");

        var rewriteProgress = new ProgressBar();
        var updatedPosts = 0;
        var doneRewrite = 0;
        foreach (var (postId, value) in contents)
        {
            try
            {
                if (!string.IsNullOrEmpty(value.Content))
                {
                    var newContent = value.Content;
                    var changed = false;
                    foreach (var (oldUrl, newUrl) in urlMap)
                    {
                        if (newContent.Contains(oldUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            newContent = newContent.Replace(oldUrl, newUrl, StringComparison.OrdinalIgnoreCase);
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        await PostWithRetryAsync(
                            $"{_config.TargetWpApiUrl}wp/v2/posts/{postId}",
                            new { content = newContent });
                        updatedPosts++;
                        await Task.Delay(150);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n    Error rewriting post {postId}: {ex.Message}");
            }
            doneRewrite++;
            rewriteProgress.Update(doneRewrite, contents.Count, $"post {postId}");
        }
        rewriteProgress.Complete($"Rewrote URLs in {updatedPosts} post(s).");

        // Delete duplicates
        Console.WriteLine($"\n  Deleting {toDelete.Count} duplicate media item(s)...");
        var delProgress = new ProgressBar();
        var deleted = 0;
        var failed = 0;
        var doneDel = 0;
        foreach (var id in toDelete)
        {
            try
            {
                var url = $"{_config.TargetWpApiUrl}wp/v2/media/{id}?force=true";
                var resp = await SendWriteAsync(() =>
                    _httpClient.SendAsync(CreateAuthenticatedRequest(HttpMethod.Delete, url)));
                if (resp.IsSuccessStatusCode) deleted++;
                else failed++;
            }
            catch { failed++; }
            doneDel++;
            delProgress.Update(doneDel, toDelete.Count, $"id={id}");
            await Task.Delay(50);
        }
        delProgress.Complete($"Deleted {deleted}, failed {failed}.");
    }

    /// <summary>
    /// Computes a dedupe key based on content identity: filesize + dimensions.
    /// Two attachments with the same bytes and dimensions are duplicates regardless
    /// of filename. For non-images (no width/height), filesize alone is the key.
    /// Returns null if neither filesize nor a usable filename is available.
    /// </summary>
    private static string? ComputeMediaDedupeKey(MediaItem m)
    {
        var size = m.MediaDetails?.FileSize;
        var w = m.MediaDetails?.Width;
        var h = m.MediaDetails?.Height;
        if (size is > 0)
            return w is > 0 && h is > 0
                ? $"size={size}|dim={w}x{h}"
                : $"size={size}";

        // No filesize → fall back to filename (rare, mostly legacy items)
        try
        {
            var fileName = Path.GetFileName(new Uri(m.SourceUrl).LocalPath);
            return string.IsNullOrEmpty(fileName) ? null : $"name={fileName.ToLowerInvariant()}";
        }
        catch
        {
            return null;
        }
    }
}
