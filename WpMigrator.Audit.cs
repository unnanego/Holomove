namespace Holomove;

public partial class WpMigrator
{
    /// <summary>
    /// Scans every target post's content for media URLs that are either
    /// (a) still pointing to the source domain (didn't get rewritten), or
    /// (b) pointing to the target domain but the file isn't in the media
    /// library (never uploaded, or uploaded under a different name).
    /// Prints problematic posts so you can manually inspect or re-run repair.
    /// </summary>
    public async Task FindBrokenMedia(int sampleLimit = 200)
    {
        await Authenticate();

        Console.WriteLine("\n  Fetching target media list...");
        await DownloadTargetMediaList();

        Console.WriteLine("  Fetching target post content...");
        var contents = await FetchTargetContents();
        Console.WriteLine($"  Got content for {contents.Count} target post(s).");

        var sourceHost = _config.SourceDomain;
        var targetHost = _config.TargetDomain;

        var sourceUrlPattern = new System.Text.RegularExpressions.Regex(
            $@"https?://(?:www\.)?{System.Text.RegularExpressions.Regex.Escape(sourceHost)}/wp-content/[^\s""'<>\)]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var sourceUrlPosts = 0;
        var missingMediaPosts = 0;
        var totalSourceUrls = 0;
        var totalMissing = 0;
        var samples = new List<(string Link, List<string> Issues)>();

        foreach (var (postId, value) in contents)
        {
            if (string.IsNullOrEmpty(value.Content)) continue;

            var hasSource = false;
            var hasMissing = false;
            var issues = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Source-host URLs anywhere in HTML (catches srcset, hrefs, etc.)
            foreach (System.Text.RegularExpressions.Match m in sourceUrlPattern.Matches(value.Content))
            {
                var url = m.Value;
                if (!seen.Add(url)) continue;
                issues.Add($"      source-url:    {url}");
                hasSource = true;
                totalSourceUrls++;
            }

            // Target-host img/video/source URLs not in media library
            foreach (var url in ExtractMediaUrls(value.Content))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                var host = uri.Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
                if (!host.Equals(targetHost, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsKnownTargetMedia(uri)) continue;
                if (!seen.Add(url)) continue;
                issues.Add($"      not-uploaded:  {url}");
                hasMissing = true;
                totalMissing++;
            }

            if (hasSource) sourceUrlPosts++;
            if (hasMissing) missingMediaPosts++;

            if (issues.Count > 0 && samples.Count < sampleLimit)
            {
                var post = _targetPosts.FirstOrDefault(p => p.Id == postId);
                var link = post?.Link ?? value.Link;
                samples.Add((link, issues));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  Posts with source-domain media URLs: {sourceUrlPosts} ({totalSourceUrls} URL(s))");
        Console.WriteLine($"  Posts with not-uploaded media:       {missingMediaPosts} ({totalMissing} URL(s))");
        Console.WriteLine($"\n  Showing {samples.Count} affected post(s):\n");
        foreach (var (link, issues) in samples)
        {
            Console.WriteLine($"  {link}");
            foreach (var iss in issues) Console.WriteLine(iss);
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Same scan as FindBrokenMedia, but actually fixes:
    ///   - source-domain URLs anywhere in HTML (src, srcset, href, inline) → upload, rewrite.
    ///   - target-domain img/video/source URLs not in media library → synthesize source URL
    ///     (host swap), upload from backup, rewrite if WP chose a different filename.
    /// Operates directly on target post content — independent of source post state.
    /// </summary>
    public async Task FixBrokenMedia()
    {
        await Authenticate();
        BuildBackupFileIndex();

        Console.WriteLine("\n  Fetching target media list...");
        await DownloadTargetMediaList();

        Console.WriteLine("  Fetching target post content...");
        var contents = await FetchTargetContents();
        Console.WriteLine($"  Got content for {contents.Count} target post(s).");

        var sourceHost = _config.SourceDomain;
        var targetHost = _config.TargetDomain;

        // Raw URL pattern: catches source-domain URLs anywhere in HTML —
        // src, srcset, href, style background, etc. Avoids missing srcset entries
        // that ExtractMediaUrls (img/video src only) skips.
        var sourceUrlPattern = new System.Text.RegularExpressions.Regex(
            $@"https?://(?:www\.)?{System.Text.RegularExpressions.Regex.Escape(sourceHost)}/wp-content/[^\s""'<>\)]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var fixedPosts = 0;
        var rewroteUrls = 0;
        var uploadedFiles = 0;
        var stillBroken = 0;
        var done = 0;
        var total = contents.Count;
        var progress = new ProgressBar();

        foreach (var (postId, value) in contents)
        {
            var slug = _targetPosts.FirstOrDefault(p => p.Id == postId)?.Slug ?? postId.ToString();
            try
            {
                if (string.IsNullOrEmpty(value.Content)) { continue; }

                var newContent = value.Content;
                var changed = false;
                var localStillBroken = 0;
                var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Pass 1: every source-host URL anywhere in content.
                foreach (System.Text.RegularExpressions.Match m in sourceUrlPattern.Matches(value.Content))
                {
                    var url = m.Value;
                    if (!processed.Add(url)) continue;

                    var media = await UploadMedia(url, attachToPostId: postId, postLink: value.Link);
                    if (media == null || string.IsNullOrEmpty(media.SourceUrl))
                    {
                        localStillBroken++;
                        continue;
                    }

                    uploadedFiles++;
                    if (media.SourceUrl != url)
                    {
                        newContent = newContent.Replace(url, media.SourceUrl);
                        changed = true;
                        rewroteUrls++;
                    }
                }

                // Pass 2: target-host img/video/source URLs whose file isn't in media library.
                foreach (var url in ExtractMediaUrls(newContent).Distinct())
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                    var host = uri.Host;
                    if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
                    if (!host.Equals(targetHost, StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsKnownTargetMedia(uri)) continue;
                    if (!processed.Add(url)) continue;

                    var srcUrl = $"{_config.SourceWpUrl}{uri.LocalPath}";
                    var media = await UploadMedia(srcUrl, attachToPostId: postId, postLink: value.Link);
                    if (media == null || string.IsNullOrEmpty(media.SourceUrl))
                    {
                        localStillBroken++;
                        continue;
                    }

                    uploadedFiles++;
                    if (media.SourceUrl != url)
                    {
                        newContent = newContent.Replace(url, media.SourceUrl);
                        changed = true;
                        rewroteUrls++;
                    }
                }

                if (changed)
                {
                    await PostWithRetryAsync(
                        $"{_config.TargetWpApiUrl}wp/v2/posts/{postId}",
                        new { content = newContent });
                    fixedPosts++;
                    await Task.Delay(150);
                }
                stillBroken += localStillBroken;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n    Error fixing {slug}: {ex.Message}");
            }
            finally
            {
                done++;
                progress.Update(done, total, slug);
            }
        }

        progress.Complete(
            $"Fixed {fixedPosts} post(s), rewrote {rewroteUrls} URL(s), " +
            $"uploaded/reused {uploadedFiles} file(s). Still broken: {stillBroken}.");
        ReportUploadErrors();
    }

    /// <summary>
    /// Returns true if the URL points to a media library item on target — either
    /// directly, by filename, by sanitized filename, or by base filename (size variant).
    /// </summary>
    private bool IsKnownTargetMedia(Uri uri)
    {
        var url = uri.ToString();
        // Strip query string for lookup
        var bare = url.Contains('?') ? url[..url.IndexOf('?')] : url;
        if (_targetMediaByUrl.ContainsKey(bare)) return true;

        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrEmpty(fileName)) return false;
        if (_targetMediaByFilename.ContainsKey(fileName)) return true;

        var sanitized = NonAsciiRegex().Replace(fileName, "");
        if (sanitized != fileName && _targetMediaByFilename.ContainsKey(sanitized)) return true;

        var (baseOrig, baseSan) = StripSizeSuffix(fileName, sanitized);
        if (baseOrig != fileName && _targetMediaByFilename.ContainsKey(baseOrig)) return true;
        if (baseSan != sanitized && _targetMediaByFilename.ContainsKey(baseSan)) return true;

        return false;
    }
}
