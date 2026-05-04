using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Holomove;

public partial class WpMigrator
{
    private const string FinalizedCacheFile = "finalized-posts.json";

    /// <summary>
    /// Rewrites media URLs in target post content from the staging/target domain
    /// to the canonical (live) domain. Run after going live so embedded image/file
    /// URLs match the public domain.
    ///
    /// Throttled (1 worker, ~200ms delay) to avoid overloading WP — every REST POST
    /// triggers save_post hooks (RankMath sitemap, revisions, schema) which pile up
    /// fast on a few-thousand-post site. Resumable via finalized-posts.json.
    /// </summary>
    public async Task FinalizeUrls()
    {
        var canonical = _config.CanonicalDomain;
        if (string.IsNullOrEmpty(canonical))
        {
            Console.WriteLine("  CanonicalDomain not set. Run `setup` to configure it.");
            return;
        }
        if (canonical.Equals(_config.TargetDomain, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  CanonicalDomain equals TargetDomain — nothing to do.");
            return;
        }

        await Authenticate();

        var finalized = LoadFinalizedCache();
        Console.WriteLine($"\n  Rewriting media URLs: {_config.TargetDomain} → {canonical}");
        if (finalized.Count > 0)
            Console.WriteLine($"  Resume: {finalized.Count} post(s) already finalized in prior run.");

        Console.WriteLine("  Fetching target post content...");
        var contents = await FetchTargetContents();
        Console.WriteLine($"  Got content for {contents.Count} target post(s).");

        var pattern = new Regex(
            $@"https?://{Regex.Escape(_config.TargetDomain)}(?=/wp-content/)",
            RegexOptions.IgnoreCase);
        var replacement = $"https://{canonical}";

        var hits = new List<(int Id, string Content, int Count)>();
        foreach (var (id, value) in contents)
        {
            if (finalized.ContainsKey(id)) continue;
            if (string.IsNullOrEmpty(value.Content)) continue;
            var count = pattern.Matches(value.Content).Count;
            if (count > 0) hits.Add((id, value.Content, count));
        }

        if (hits.Count == 0)
        {
            Console.WriteLine("  No matching URLs found (or all already finalized).");
            return;
        }

        Console.WriteLine($"\n  Will rewrite {hits.Sum(h => h.Count)} URL(s) across {hits.Count} post(s).");
        Console.WriteLine("  Sample (first 3):");
        foreach (var (id, content, _) in hits.Take(3))
        {
            var first = pattern.Match(content);
            var slug = _targetPosts.FirstOrDefault(p => p.Id == id)?.Slug ?? id.ToString();
            Console.WriteLine($"    {slug}: {first.Value}/wp-content/...  →  {replacement}/wp-content/...");
        }
        Console.Write("\n  Apply? [y/N]: ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (input != "y" && input != "yes")
        {
            Console.WriteLine("  Aborted.");
            return;
        }

        var updated = 0;
        var totalReplaced = 0;
        var failed = 0;
        var done = 0;
        var total = hits.Count;
        var progress = new ProgressBar();
        var saveLock = new object();

        // Sequential + small delay. Each REST POST triggers WP save hooks
        // (RankMath sitemap, revisions, schema) which pile up under parallel load.
        foreach (var hit in hits)
        {
            var slug = _targetPosts.FirstOrDefault(p => p.Id == hit.Id)?.Slug ?? hit.Id.ToString();
            try
            {
                var newContent = pattern.Replace(hit.Content, replacement);
                var ok = await PostWithRetryAsync(
                    $"{_config.TargetWpApiUrl}wp/v2/posts/{hit.Id}",
                    new { content = newContent });

                if (ok)
                {
                    finalized[hit.Id] = true;
                    updated++;
                    totalReplaced += hit.Count;

                    // Persist cache every 25 posts so a crash doesn't redo work.
                    if (updated % 25 == 0) lock (saveLock) SaveFinalizedCache(finalized);
                }
                else
                {
                    failed++;
                    Console.WriteLine($"\n    Failed (gave up after retries): {slug}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"\n    Error finalizing {slug}: {ex.Message}");
            }

            done++;
            progress.Update(done, total, slug);
            await Task.Delay(200);
        }

        lock (saveLock) SaveFinalizedCache(finalized);
        progress.Complete($"Replaced {totalReplaced} URL(s) across {updated} post(s). Failed: {failed}.");
    }

    private async Task<bool> PostWithRetryAsync(string url, object payload, int maxRetries = 5)
    {
        var delay = TimeSpan.FromSeconds(2);
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await PostJsonAsync(url, payload);
                if (response.IsSuccessStatusCode) return true;

                // 5xx / 408 / 429 → retry. 4xx (except 408/429) → bail.
                var code = (int)response.StatusCode;
                var transient = code is 408 or 429 or >= 500;
                if (!transient) return false;
            }
            catch (TaskCanceledException) { /* timeout — retry */ }
            catch (HttpRequestException) { /* network blip — retry */ }

            if (attempt < maxRetries)
            {
                await Task.Delay(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 30_000));
            }
        }
        return false;
    }

    private ConcurrentDictionary<int, bool> LoadFinalizedCache()
    {
        var cache = new ConcurrentDictionary<int, bool>();
        var path = Path.Combine(_backupRoot, FinalizedCacheFile);
        if (!File.Exists(path)) return cache;

        try
        {
            var ids = JsonConvert.DeserializeObject<List<int>>(File.ReadAllText(path)) ?? [];
            foreach (var id in ids) cache[id] = true;
        }
        catch { /* ignore corrupt cache */ }
        return cache;
    }

    private void SaveFinalizedCache(ConcurrentDictionary<int, bool> cache)
    {
        var ids = cache.Keys.OrderBy(i => i).ToList();
        File.WriteAllText(
            Path.Combine(_backupRoot, FinalizedCacheFile),
            JsonConvert.SerializeObject(ids));
    }
}
