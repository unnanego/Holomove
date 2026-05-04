using AngleSharp.Html.Parser;

namespace Holomove;

public partial class WpMigrator
{
    /// <summary>
    /// Walks every target post's content and strips srcset/sizes from all <img> tags.
    /// After this, target's wp_filter_content_tags will rebuild responsive attributes
    /// on render based on TARGET's media library — eliminating broken source-host srcset
    /// entries and stale-size references in one pass.
    /// </summary>
    public async Task CleanSrcset()
    {
        await Authenticate();

        Console.WriteLine("\n  Fetching target post content...");
        var contents = await FetchTargetContents();
        Console.WriteLine($"  Got content for {contents.Count} target post(s).");

        var parser = new HtmlParser();
        var updated = 0;
        var totalStripped = 0;
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
                var imgs = doc.QuerySelectorAll("img[srcset], img[sizes]").ToList();
                if (imgs.Count == 0) continue;

                foreach (var img in imgs)
                {
                    img.RemoveAttribute("srcset");
                    img.RemoveAttribute("sizes");
                }

                var newContent = doc.Body?.InnerHtml ?? value.Content;
                if (newContent == value.Content) continue;

                await PostWithRetryAsync(
                    $"{_config.TargetWpApiUrl}wp/v2/posts/{postId}",
                    new { content = newContent });
                updated++;
                totalStripped += imgs.Count;
                await Task.Delay(120);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n    Error cleaning {slug}: {ex.Message}");
            }
            done++;
            progress.Update(done, total, slug);
        }

        progress.Complete($"Stripped srcset/sizes from {totalStripped} img tag(s) across {updated} post(s).");
    }
}
