using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Holomove;

public partial class WpMigrator
{
    /// <summary>
    /// Replaces broken Newspaper-theme custom slider galleries (div.td-gallery)
    /// with native WordPress gallery blocks. The slider's JS/CSS was removed in
    /// a Newspaper update, leaving the raw markup unrendered. Also strips the
    /// preceding &lt;style&gt; block that targeted the now-defunct slider.
    /// </summary>
    public async Task FixGalleries(bool testOne = false)
    {
        await Authenticate();

        Console.WriteLine("\n  Fetching target post content...");
        var contents = await FetchTargetContents();
        Console.WriteLine($"  Got content for {contents.Count} target post(s).");

        var parser = new HtmlParser();
        var fixedPosts = 0;
        var totalGalleries = 0;
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
                var galleries = doc.QuerySelectorAll("div.td-gallery").ToList();
                if (galleries.Count == 0) continue;

                foreach (var gallery in galleries)
                {
                    // Collect img src URLs from primary slider; fall back to any img in gallery.
                    var primaryImgs = gallery.QuerySelectorAll(".td-doubleSlider-1 .td-slide-item img");
                    var fallbackImgs = gallery.QuerySelectorAll("img");
                    var imgs = primaryImgs.Length > 0 ? primaryImgs : fallbackImgs;
                    var srcs = imgs
                        .Select(i => i.GetAttribute("src"))
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Cast<string>()
                        .ToList();

                    if (testOne)
                    {
                        Console.WriteLine($"\n  [diag] gallery primary-selector imgs: {primaryImgs.Length}, " +
                                          $"fallback any-img: {fallbackImgs.Length}, srcs collected: {srcs.Count}");
                    }

                    if (srcs.Count == 0)
                    {
                        Console.WriteLine($"\n    Skipping gallery in {slug}: parser found no imgs (left untouched).");
                        continue;
                    }

                    // Strip preceding <style> block(s) targeting this slider markup.
                    var prev = gallery.PreviousElementSibling;
                    while (prev != null && string.Equals(prev.LocalName, "style", StringComparison.OrdinalIgnoreCase))
                    {
                        var styleText = prev.TextContent ?? "";
                        if (styleText.Contains("td-doubleSlider", StringComparison.Ordinal)
                            || styleText.Contains("td-gallery", StringComparison.Ordinal))
                        {
                            var toRemove = prev;
                            prev = prev.PreviousElementSibling;
                            toRemove.Remove();
                            continue;
                        }
                        break;
                    }

                    // Build native WP gallery block markup.
                    var sb = new StringBuilder();
                    sb.Append("<!-- wp:gallery {\"linkTo\":\"none\"} -->\n");
                    sb.Append("<figure class=\"wp-block-gallery has-nested-images columns-default is-cropped\">\n");
                    foreach (var src in srcs)
                    {
                        sb.Append("<!-- wp:image {\"linkDestination\":\"none\"} -->\n");
                        sb.Append("<figure class=\"wp-block-image\"><img src=\"")
                          .Append(WebUtility.HtmlEncode(src))
                          .Append("\" alt=\"\"/></figure>\n");
                        sb.Append("<!-- /wp:image -->\n");
                    }
                    sb.Append("</figure>\n");
                    sb.Append("<!-- /wp:gallery -->");

                    gallery.OuterHtml = sb.ToString();
                    totalGalleries++;
                }

                var newContent = doc.Body?.InnerHtml ?? value.Content;
                if (newContent == value.Content) continue;

                if (testOne)
                {
                    Console.WriteLine($"\n  Test post: {value.Link}");
                    Console.WriteLine($"  Slug:      {slug}");
                    Console.WriteLine($"  Galleries replaced in this post: {totalGalleries}");
                    Console.WriteLine("\n  --- new gallery markup (first 1500 chars) ---");
                    var snippet = newContent.Length > 1500 ? newContent[..1500] + "…" : newContent;
                    Console.WriteLine(snippet);
                    Console.WriteLine("\n  --- end snippet ---");
                    Console.Write("\n  Push this single post? [y/N]: ");
                    var input = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (input != "y" && input != "yes")
                    {
                        Console.WriteLine("  Aborted, nothing written.");
                        return;
                    }
                    await PostWithRetryAsync(
                        $"{_config.TargetWpApiUrl}wp/v2/posts/{postId}",
                        new { content = newContent });
                    Console.WriteLine($"  Pushed. Inspect on target: {value.Link}");
                    return;
                }

                await PostWithRetryAsync(
                    $"{_config.TargetWpApiUrl}wp/v2/posts/{postId}",
                    new { content = newContent });
                fixedPosts++;
                await Task.Delay(150);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n    Error fixing gallery in {slug}: {ex.Message}");
            }
            done++;
            progress.Update(done, total, slug);
        }

        if (testOne)
        {
            Console.WriteLine("\n  No posts with td-gallery found.");
            return;
        }

        progress.Complete($"Replaced {totalGalleries} gallery/-ies across {fixedPosts} post(s).");
    }
}