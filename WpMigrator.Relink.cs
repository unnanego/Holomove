using System.Text.RegularExpressions;

namespace Holomove;

public partial class WpMigrator
{
    public async Task Relink()
    {
        await FetchTargetData();
        BuildLookupDictionaries();

        Console.WriteLine("\n  Fetching target post content...");
        var contents = await FetchTargetContents();
        Console.WriteLine($"  Got content for {contents.Count} target post(s).");

        var ctx = BuildResolverContext();

        // Pre-pass: scan all posts, count fixes/unresolved, collect samples.
        Console.WriteLine("\n  Scanning for stale internal post-id links...");
        var totalFixes = 0;
        var totalUnresolved = 0;
        var postsWithFixes = 0;
        var unresolvedSamples = new List<(string Link, List<(string OldUrl, string Status)> Issues)>();
        const int sampleLimit = 10;

        foreach (var kvp in contents)
        {
            var content = kvp.Value.Content;
            if (string.IsNullOrEmpty(content)) continue;

            var entries = ScanLinks(content, ctx);
            if (entries.Count == 0) continue;

            var fixesThisPost = entries.Count(e => e.NewUrl != null);
            var unresolvedThisPost = entries.Where(e => e.NewUrl == null).ToList();
            if (fixesThisPost > 0) postsWithFixes++;
            totalFixes += fixesThisPost;
            totalUnresolved += unresolvedThisPost.Count;

            if (unresolvedSamples.Count < sampleLimit && unresolvedThisPost.Count > 0)
            {
                var post = _targetPosts.FirstOrDefault(p => p.Id == kvp.Key);
                var link = StripStagingHost(post?.Link ?? kvp.Value.Link);
                unresolvedSamples.Add((link, unresolvedThisPost.Select(e => (e.OldUrl, e.Status)).ToList()));
            }
        }

        Console.WriteLine($"\n  Resolvable: {totalFixes} link(s) across {postsWithFixes} post(s)");
        Console.WriteLine($"  Unresolved (won't be touched): {totalUnresolved}");

        if (unresolvedSamples.Count > 0)
        {
            Console.WriteLine($"\n  First {unresolvedSamples.Count} post(s) with unresolved links:");
            foreach (var (link, issues) in unresolvedSamples)
            {
                Console.WriteLine($"    {link}");
                foreach (var (url, status) in issues)
                    Console.WriteLine($"      {status.ToUpper()}: {url}");
            }
        }

        if (totalFixes == 0)
        {
            Console.WriteLine("\n  Nothing to apply.");
            return;
        }

        Console.Write("\n  Apply replacements? [y/N]: ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (input != "y" && input != "yes")
        {
            Console.WriteLine("  Aborted.");
            return;
        }

        var updated = 0;
        var totalReplaced = 0;
        var ambiguous = 0;
        var missing = 0;
        var done = 0;
        var total = contents.Count;
        var progress = new ProgressBar();

        await Parallel.ForEachAsync(contents, new ParallelOptions { MaxDegreeOfParallelism = 5 },
            async (kvp, _) =>
            {
                var postId = kvp.Key;
                var content = kvp.Value.Content;
                var slug = _targetPosts.FirstOrDefault(p => p.Id == postId)?.Slug ?? postId.ToString();

                try
                {
                    if (string.IsNullOrEmpty(content)) return;

                    var entries = ScanLinks(content, ctx);
                    if (entries.Count == 0) return;

                    var localReplaced = 0;
                    var localAmbiguous = 0;
                    var localMissing = 0;

                    var newContent = content;
                    foreach (var (oldUrl, newUrl, status) in entries.DistinctBy(e => e.OldUrl))
                    {
                        switch (status)
                        {
                            case "fix" when newUrl != null:
                                newContent = newContent.Replace(oldUrl, newUrl);
                                localReplaced++;
                                break;
                            case "no-match":
                                localMissing++;
                                break;
                            default:
                                localAmbiguous++;
                                break;
                        }
                    }

                    Interlocked.Add(ref ambiguous, localAmbiguous);
                    Interlocked.Add(ref missing, localMissing);

                    if (localReplaced == 0) return;

                    await PostJsonAsync(
                        $"{_config.TargetWpApiUrl}wp/v2/posts/{postId}",
                        new { content = newContent });

                    Interlocked.Increment(ref updated);
                    Interlocked.Add(ref totalReplaced, localReplaced);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n    Error relinking {slug}: {ex.Message}");
                }
                finally
                {
                    var count = Interlocked.Increment(ref done);
                    progress.Update(count, total, slug);
                }
            });

        progress.Complete(
            $"Relinked {totalReplaced} URL(s) across {updated} post(s). " +
            $"Skipped: {ambiguous} ambiguous, {missing} no-match.");
    }

    private async Task<Dictionary<int, (string Link, string Content)>> FetchTargetContents()
    {
        var result = new Dictionary<int, (string Link, string Content)>();
        var resultLock = new object();
        const string fields = "_fields=id,slug,link,content";

        var (firstPage, totalPages) = await FetchPageAsync<WpPost>(
            _config.TargetWpApiUrl, "posts", 1, useAuth: true, fields);

        void Merge(List<WpPost> posts)
        {
            lock (resultLock)
                foreach (var p in posts)
                    result[p.Id] = (p.Link, p.Content?.Rendered ?? "");
        }

        Merge(firstPage);
        var width = Math.Max(Console.WindowWidth, 80);
        Console.Write($"\r  Fetching target content: page 1/{totalPages}".PadRight(width - 1));

        if (totalPages > 1)
        {
            var pagesDone = 1;
            await Parallel.ForEachAsync(Enumerable.Range(2, totalPages - 1),
                new ParallelOptions { MaxDegreeOfParallelism = 6 },
                async (page, _) =>
                {
                    var (posts, _) = await FetchPageAsync<WpPost>(
                        _config.TargetWpApiUrl, "posts", page, useAuth: true, fields);
                    Merge(posts);
                    var n = Interlocked.Increment(ref pagesDone);
                    Console.Write($"\r  Fetching target content: page {n}/{totalPages}".PadRight(width - 1));
                });
        }

        Console.WriteLine();

        // Backfill Link on _targetPosts so stem map can use it.
        foreach (var p in _targetPosts)
            if (string.IsNullOrEmpty(p.Link) && result.TryGetValue(p.Id, out var v))
                p.Link = v.Link;

        return result;
    }

    private record ResolverContext(
        Regex HrefRegex,
        Regex PathTailRegex,
        Regex StemRegex,
        Dictionary<string, List<WpPost>> StemGroups,
        Dictionary<string, List<WpPost>> NumGroups,
        HashSet<string> TargetSlugs,
        string SourceHost,
        string TargetHost);

    private ResolverContext BuildResolverContext()
    {
        var stemRegex = StemRegex();
        var indexed = _targetPosts
            .Select(p => new { post = p, m = stemRegex.Match(p.Slug) })
            .Where(x => x.m.Success)
            .ToList();

        // Stem-keyed: catches post-id-changed case (stem same, number differs).
        var stemGroups = indexed
            .GroupBy(x => x.m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.post).ToList(), StringComparer.OrdinalIgnoreCase);

        // Number-keyed: catches stem-typo case (number same, dashes shifted).
        var numGroups = indexed
            .GroupBy(x => x.m.Groups[2].Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.post).ToList());

        return new ResolverContext(
            HrefRegex(),
            PathTailRegex(),
            stemRegex,
            stemGroups,
            numGroups,
            _targetPosts.Select(p => p.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase),
            NormalizeHost(_config.SourceDomain),
            NormalizeHost(_config.TargetDomain));
    }

    private List<(string OldUrl, string? NewUrl, string Status)> ScanLinks(string content, ResolverContext ctx)
    {
        var result = new List<(string, string?, string)>();
        foreach (Match m in ctx.HrefRegex.Matches(content))
        {
            var url = m.Groups[2].Value;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            var host = NormalizeHost(uri.Host);
            if (!host.Equals(ctx.SourceHost, StringComparison.OrdinalIgnoreCase) &&
                !host.Equals(ctx.TargetHost, StringComparison.OrdinalIgnoreCase)) continue;
            var path = uri.AbsolutePath.TrimEnd('/');
            if (IsArchivePath(path)) continue;
            var lastSlash = path.LastIndexOf('/');
            if (lastSlash < 0) continue;
            var linkSlug = path[(lastSlash + 1)..];
            if (string.IsNullOrEmpty(linkSlug)) continue;
            if (ctx.TargetSlugs.Contains(linkSlug)) continue;

            var t = ctx.PathTailRegex.Match(linkSlug);
            if (!t.Success) continue;
            var stem = t.Groups[1].Value;
            var num = t.Groups[2].Value;
            var stemNorm = NormalizeStem(stem);

            var candidates = new HashSet<WpPost>();

            // Stem match: exact stem, any post-id suffix.
            if (ctx.StemGroups.TryGetValue(stem, out var byStem))
                foreach (var p in byStem) candidates.Add(p);

            // Number match (≥3-digit only): same post-id, stems related by:
            //   - normalized equality (dash-shift / typo)
            //   - whole-word prefix in either direction (rename added or removed trailing words)
            if (num.Length >= 3 && ctx.NumGroups.TryGetValue(num, out var byNum))
                foreach (var p in byNum)
                {
                    var tm = ctx.StemRegex.Match(p.Slug);
                    if (!tm.Success) continue;
                    var pStem = tm.Groups[1].Value;
                    if (NormalizeStem(pStem).Equals(stemNorm, StringComparison.OrdinalIgnoreCase) ||
                        IsWordPrefix(stem, pStem) || IsWordPrefix(pStem, stem))
                        candidates.Add(p);
                }

            if (candidates.Count == 0)
            {
                result.Add((url, null, "no-match"));
                continue;
            }
            if (candidates.Count > 1)
            {
                result.Add((url, null, $"ambiguous({candidates.Count})"));
                continue;
            }
            var match = candidates.First();
            var newUrl = !string.IsNullOrEmpty(match.Link)
                ? match.Link
                : $"{_config.TargetWpUrl}/{match.Slug}/";
            result.Add((url, StripStagingHost(newUrl), "fix"));
        }
        return result;
    }

    private static string NormalizeStem(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool IsWordPrefix(string shorter, string longer) =>
        longer.Length > shorter.Length &&
        longer.StartsWith(shorter + "-", StringComparison.OrdinalIgnoreCase);

    private static string StripStagingHost(string url) =>
        string.IsNullOrEmpty(url) ? url : Regex.Replace(url, @"://new\.", "://", RegexOptions.IgnoreCase);

    private static readonly string[] ArchivePrefixes =
        ["tag", "category", "author", "page", "feed", "wp-content", "wp-admin", "wp-json", "comments"];

    private static bool IsArchivePath(string path)
    {
        var trimmed = path.TrimStart('/');
        var firstSlash = trimmed.IndexOf('/');
        var firstSegment = firstSlash < 0 ? trimmed : trimmed[..firstSlash];
        return ArchivePrefixes.Contains(firstSegment, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return host;
        host = host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        if (host.StartsWith("new.")) host = host[4..];
        return host;
    }

    [GeneratedRegex(@"^(.+)-(\d{3,})$")]
    private static partial Regex StemRegex();

    [GeneratedRegex("""(href\s*=\s*["'])([^"']+)(["'])""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"^(.+)-(\d{2,})$")]
    private static partial Regex PathTailRegex();
}
