using AngleSharp.Html.Parser;

namespace Holomove;

public partial class WpMigrator
{
    /// <summary>
    /// Removes &lt;a href&gt; wrappers around &lt;img&gt; tags whose href points
    /// to a wp-content file that no longer exists — source-domain URLs (always
    /// stale on target) and target-domain URLs not present in the media library.
    /// Image src still loads (served from source WP), but the click-through link
    /// is broken. Drops the link, keeps the image. Leaves anchors that wrap text
    /// + image alone — only fully image-only anchors are unwrapped.
    /// </summary>
    public async Task UnwrapBrokenImageLinks()
    {
        await Authenticate();

        Console.WriteLine("\n  Fetching target media list...");
        await DownloadTargetMediaList();

        Console.WriteLine("  Fetching target post content...");
        var contents = await FetchTargetContents();
        Console.WriteLine($"  Got content for {contents.Count} target post(s).");

        var parser = new HtmlParser();
        var sourceHost = _config.SourceDomain;
        var targetHost = _config.TargetDomain;
        var fixedPosts = 0;
        var totalUnwrapped = 0;
        var done = 0;
        var total = contents.Count;
        var progress = new ProgressBar();

        foreach (var (postId, value) in contents)
        {
            var slug = _targetPosts.FirstOrDefault(p => p.Id == postId)?.Slug ?? postId.ToString();
            try
            {
                if (string.IsNullOrEmpty(value.Content)) continue;

                var doc = parser.ParseDocument($"<body>{value.Content}</body>");
                var anchors = doc.QuerySelectorAll("a[href]")
                    .Where(a => a.QuerySelector("img") != null)
                    .Where(a => string.IsNullOrWhiteSpace(a.TextContent))
                    .ToList();

                var localUnwrapped = 0;
                foreach (var a in anchors)
                {
                    var href = a.GetAttribute("href") ?? "";
                    if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;
                    if (!uri.LocalPath.Contains("/wp-content/", StringComparison.OrdinalIgnoreCase)) continue;

                    var host = uri.Host;
                    if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];

                    bool broken;
                    if (host.Equals(sourceHost, StringComparison.OrdinalIgnoreCase))
                        broken = true;
                    else if (host.Equals(targetHost, StringComparison.OrdinalIgnoreCase))
                        broken = !IsKnownTargetMedia(uri);
                    else continue;

                    if (!broken) continue;

                    var parent = a.ParentElement;
                    if (parent == null) continue;
                    while (a.FirstChild != null)
                        parent.InsertBefore(a.FirstChild, a);
                    a.Remove();
                    localUnwrapped++;
                }

                if (localUnwrapped == 0) continue;

                var newContent = doc.Body?.InnerHtml ?? value.Content;
                if (newContent == value.Content) continue;

                await PostWithRetryAsync(
                    $"{_config.TargetWpApiUrl}wp/v2/posts/{postId}",
                    new { content = newContent });
                fixedPosts++;
                totalUnwrapped += localUnwrapped;
                await Task.Delay(150);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n    Error unwrapping in {slug}: {ex.Message}");
            }
            done++;
            progress.Update(done, total, slug);
        }

        progress.Complete($"Unwrapped {totalUnwrapped} broken image link(s) across {fixedPosts} post(s).");
    }
}
