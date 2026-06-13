using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Holomove;

public partial class WpMigrator
{
    // The broken Newspaper slider markup is riddled with wpautop-inserted <p> tags
    // (even inside <style> and between the slider divs), which makes a real HTML5
    // parser foster-parent the slide <img>s out of the gallery element — so any
    // DOM-scoped query under div.td-gallery finds nothing. We therefore work on the
    // RAW content: locate each gallery container by balanced-<div> matching and pull
    // the slide image URLs straight from the clean
    // `<a class="slide-gallery-image-link" href="…">` anchors (falling back to <img src>).
    // td-gallery as a whole class token only — NOT td-gallery-slide-top / -title / etc.
    // (a hyphen counts as a \b word boundary, so \btd-gallery\b wrongly matched the
    // child control divs; the lookbehind/lookahead require the token to end at a
    // space or the closing quote).
    private static readonly Regex GalleryOpenRegex = new(
        "<div\\b[^>]*class\\s*=\\s*\"[^\"]*(?<![-\\w])td-gallery(?![-\\w])[^\"]*\"[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DivTokenRegex = new(
        "<div\\b|</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SlideLinkRegex = new(
        "class\\s*=\\s*\"slide-gallery-image-link\"[^>]*?\\shref\\s*=\\s*\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SlideImgRegex = new(
        "<img\\b[^>]*\\ssrc\\s*=\\s*\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrecedingStyleRegex = new(
        "<style\\b[^>]*>.*?</style>\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Replaces broken Newspaper-theme custom slider galleries (div.td-gallery) with
    /// native WordPress gallery blocks. The slider's JS/CSS was removed in a Newspaper
    /// update, leaving raw unrendered markup; this rebuilds it from the slide image URLs
    /// and strips the now-defunct preceding &lt;style&gt; block.
    /// </summary>
    public async Task FixGalleries(bool testOne = false)
    {
        await Authenticate();

        Console.WriteLine("\n  Fetching target post content...");
        var contents = await FetchTargetContents();
        Console.WriteLine($"  Got content for {contents.Count} target post(s).");

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
                var content = value.Content;
                if (string.IsNullOrEmpty(content) ||
                    !content.Contains("td-gallery", StringComparison.OrdinalIgnoreCase))
                {
                    done++; progress.Update(done, total, slug); continue;
                }

                var (newContent, replaced) = RebuildGalleries(content, slug, testOne);
                if (replaced == 0 || newContent == content)
                {
                    done++; progress.Update(done, total, slug); continue;
                }

                totalGalleries += replaced;

                if (testOne)
                {
                    Console.WriteLine($"\n  Test post: {value.Link}");
                    Console.WriteLine($"  Slug:      {slug}");
                    Console.WriteLine($"  Galleries replaced in this post: {replaced}");
                    Console.WriteLine("\n  --- new markup (first 1500 chars) ---");
                    Console.WriteLine(newContent.Length > 1500 ? newContent[..1500] + "…" : newContent);
                    Console.WriteLine("\n  --- end snippet ---");
                    Console.Write("\n  Push this single post? [y/N]: ");
                    var input = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (input != "y" && input != "yes")
                    {
                        Console.WriteLine("  Aborted, nothing written.");
                        return;
                    }
                    await PostWithRetryAsync(
                        $"{_config.TargetWpApiUrl}wp/v2/posts/{postId}", new { content = newContent });
                    Console.WriteLine($"  Pushed. Inspect on target: {value.Link}");
                    return;
                }

                await PostWithRetryAsync(
                    $"{_config.TargetWpApiUrl}wp/v2/posts/{postId}", new { content = newContent });
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
            Console.WriteLine("\n  No replaceable td-gallery found.");
            return;
        }

        progress.Complete($"Replaced {totalGalleries} gallery/-ies across {fixedPosts} post(s).");
    }

    /// <summary>
    /// Find each td-gallery container span by balanced-div matching and replace it (plus a
    /// preceding slider &lt;style&gt; block) with a native gallery block built from its slide
    /// image URLs. Returns the new content and the number of galleries replaced. Galleries
    /// with no extractable slide URL are left untouched.
    /// </summary>
    private (string Content, int Replaced) RebuildGalleries(string content, string slug, bool diag)
    {
        // Pass 1: analyse the ORIGINAL content (read-only) and record each replacement as
        // (start, end, block). Mutating while using match indices from the original string
        // is what corrupted indices before, so analysis and mutation are kept separate.
        var edits = new List<(int Start, int End, string Block)>();
        foreach (Match open in GalleryOpenRegex.Matches(content))
        {
            var end = MatchingDivEnd(content, open.Index);
            if (end < 0) continue;
            var span = content[open.Index..end];

            var urls = new List<string>();
            foreach (Match m in SlideLinkRegex.Matches(span))
            {
                var u = WebUtility.HtmlDecode(m.Groups[1].Value);
                if (!urls.Contains(u)) urls.Add(u);
            }
            if (urls.Count == 0)
            {
                foreach (Match m in SlideImgRegex.Matches(span))
                {
                    var u = WebUtility.HtmlDecode(m.Groups[1].Value);
                    if (!urls.Contains(u)) urls.Add(u);
                }
            }

            if (diag)
            {
                Console.WriteLine($"\n  [diag] {slug}: gallery span {span.Length}B, slide URLs found: {urls.Count}");
                foreach (var u in urls.Take(4)) Console.WriteLine($"        {u}");
            }

            if (urls.Count == 0)
            {
                Console.WriteLine($"\n    Skipping gallery in {slug}: no slide URLs found (left untouched).");
                continue;
            }

            // Swallow a defunct slider <style> block immediately preceding the gallery.
            var start = open.Index;
            var styleMatch = PrecedingStyleRegex.Match(content[..start]);
            if (styleMatch.Success &&
                (styleMatch.Value.Contains("td-doubleSlider", StringComparison.Ordinal) ||
                 styleMatch.Value.Contains("td-gallery", StringComparison.Ordinal)))
                start = styleMatch.Index;

            edits.Add((start, end, BuildGalleryBlock(urls)));
        }

        // Some posts have malformed cross-gallery div nesting, so one gallery's balanced
        // span can engulf a sibling. Keep the OUTERMOST non-overlapping spans (the engulfed
        // sibling's images are then captured in the outer gallery) and drop the contained
        // ones, otherwise an overlapped gallery is left behind on every run.
        var kept = new List<(int Start, int End, string Block)>();
        var boundary = -1;
        foreach (var e in edits.OrderBy(e => e.Start).ThenByDescending(e => e.End))
            if (e.Start >= boundary) { kept.Add(e); boundary = e.End; }

        // Apply right-to-left so each splice only touches content after the edit.
        foreach (var e in kept.OrderByDescending(e => e.Start))
            content = content[..e.Start] + e.Block + content[e.End..];
        return (content, kept.Count);
    }

    /// <summary>Index just past the &lt;/div&gt; that closes the &lt;div&gt; starting at openStart.</summary>
    private static int MatchingDivEnd(string s, int openStart)
    {
        var gt = s.IndexOf('>', openStart);
        if (gt < 0) return -1;
        var depth = 1;
        foreach (Match m in DivTokenRegex.Matches(s, gt + 1))
        {
            if (m.Value[1] == '/') { if (--depth == 0) return m.Index + m.Length; }
            else depth++;
        }
        return -1;
    }

    private static string BuildGalleryBlock(List<string> urls)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- wp:gallery {\"linkTo\":\"none\"} -->\n");
        sb.Append("<figure class=\"wp-block-gallery has-nested-images columns-default is-cropped\">\n");
        foreach (var src in urls)
        {
            sb.Append("<!-- wp:image {\"linkDestination\":\"none\"} -->\n");
            sb.Append("<figure class=\"wp-block-image\"><img src=\"")
              .Append(WebUtility.HtmlEncode(src))
              .Append("\" alt=\"\"/></figure>\n");
            sb.Append("<!-- /wp:image -->\n");
        }
        sb.Append("</figure>\n");
        sb.Append("<!-- /wp:gallery -->");
        return sb.ToString();
    }
}
