using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace Holomove;

public partial class WpMigrator
{
    private static string ProcessShortcodes(string content)
    {
        // Process [embed] shortcodes for YouTube/Vimeo
        content = YoutubeEmbedRegex().Replace(content, m => $"""<figure class="wp-block-embed is-type-video is-provider-youtube"><div class="wp-block-embed__wrapper"><iframe width="560" height="315" src="https://www.youtube.com/embed/{m.Groups[2].Value}" frameborder="0" allowfullscreen></iframe></div></figure>""");
        content = YoutubeShortEmbedRegex().Replace(content, m => $"""<figure class="wp-block-embed is-type-video is-provider-youtube"><div class="wp-block-embed__wrapper"><iframe width="560" height="315" src="https://www.youtube.com/embed/{m.Groups[2].Value}" frameborder="0" allowfullscreen></iframe></div></figure>""");
        content = VimeoEmbedRegex().Replace(content, m => $"""<figure class="wp-block-embed is-type-video is-provider-vimeo"><div class="wp-block-embed__wrapper"><iframe src="https://player.vimeo.com/video/{m.Groups[2].Value}" width="560" height="315" frameborder="0" allowfullscreen></iframe></div></figure>""");

        // Remove TablePress shortcodes
        content = TablePressShortcodeRegex().Replace(content, "");

        // Process [video] shortcodes
        content = VideoShortcodeRegex().Replace(content, m => $"""<figure class="wp-block-video"><video controls src="{m.Groups[1].Value}"></video></figure>""");

        return content;
    }

    private static string CleanupHtml(string content)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument($"<body>{content}</body>");

        // Strip srcset/sizes from <img> tags. WP only auto-injects responsive
        // attributes when missing, so leaving source's pre-baked srcset means
        // target keeps source-host URLs and can't pick up target's actual sizes.
        // Letting target's wp_filter_content_tags rebuild on render = always correct.
        foreach (var img in doc.QuerySelectorAll("img[srcset], img[sizes]"))
        {
            img.RemoveAttribute("srcset");
            img.RemoveAttribute("sizes");
        }

        // Clean up TablePress tables
        foreach (var table in doc.QuerySelectorAll("table.tablepress"))
        {
            var currentClass = table.ClassName ?? "";
            var newClass = TablePressClassRegex().Replace(currentClass, "").Trim();
            if (string.IsNullOrEmpty(newClass))
                table.RemoveAttribute("class");
            else
                table.ClassName = newClass;

            foreach (var attr in table.Attributes.Where(a => a.Name.StartsWith("data-")).ToList())
                table.RemoveAttribute(attr.Name);
        }

        // Remove TablePress wrapper divs
        foreach (var wrapper in doc.QuerySelectorAll("div.tablepress-scroll-wrapper").ToList())
        {
            wrapper.OuterHtml = wrapper.InnerHtml;
        }

        // Wrap standalone iframes in figure tags
        foreach (var iframe in doc.QuerySelectorAll("iframe:not(figure iframe)").ToList())
        {
            var figure = doc.CreateElement("figure");
            figure.ClassName = "wp-block-embed";
            var wrapperDiv = doc.CreateElement("div");
            wrapperDiv.ClassName = "wp-block-embed__wrapper";
            wrapperDiv.InnerHtml = iframe.OuterHtml;
            figure.AppendChild(wrapperDiv);
            iframe.Replace(figure);
        }

        return doc.Body?.InnerHtml ?? content;
    }

    [GeneratedRegex(@"tablepress\s*tablepress-id-\d+\s*")]
    private static partial Regex TablePressClassRegex();
    [GeneratedRegex(@"\[embed\](https?://(?:www\.)?youtube\.com/watch\?v=([a-zA-Z0-9_-]+)[^\[]*)\[/embed\]", RegexOptions.IgnoreCase, "en-AE")]
    private static partial Regex YoutubeEmbedRegex();
    [GeneratedRegex(@"\[embed\](https?://(?:www\.)?youtu\.be/([a-zA-Z0-9_-]+)[^\[]*)\[/embed\]", RegexOptions.IgnoreCase, "en-AE")]
    private static partial Regex YoutubeShortEmbedRegex();
    [GeneratedRegex("""\[video\s+[^\]]*(?:mp4|src)=["']([^"']+)["'][^\]]*\](?:\[/video\])?""", RegexOptions.IgnoreCase, "en-AE")]
    private static partial Regex VideoShortcodeRegex();
    [GeneratedRegex(@"\[/?table(?:press)?[^\]]*\]", RegexOptions.IgnoreCase, "en-AE")]
    private static partial Regex TablePressShortcodeRegex();
    [GeneratedRegex(@"\[embed\](https?://(?:www\.)?vimeo\.com/(\d+)[^\[]*)\[/embed\]", RegexOptions.IgnoreCase, "en-AE")]
    private static partial Regex VimeoEmbedRegex();
}
