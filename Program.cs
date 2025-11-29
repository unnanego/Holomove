using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WordPressPCL;
using WordPressPCL.Models;

var html = new HtmlDocument();

// Data files location
const string dataFolder = @"C:\Users\unnanego\Desktop\downloads";
Directory.CreateDirectory(dataFolder);

// Source: WordPress
var wp = new WordPressClient("https://holographica.space/wp-json/");
// Target: Ghost at new.holographica.space
const string ghostUrl = "https://new.holographica.space";
const string ghostAdminApiKey = "69288e148eb69d0351ab7933:0f24e27f99e1af24e71a4817d8783636f9b808301cb99896c640a435ec0a03c4"; // Format: {id}:{secret} - Get from Ghost Admin > Settings > Integrations

// Ghost API client setup
var ghostClient = new GhostAdminClient(ghostUrl, ghostAdminApiKey);

List<Post>? wpPosts = null;
List<Tag>? wpTags = null;
List<User>? wpAuthors = null;
List<Category>? wpCategories = null;
List<GhostPost>? ghostPosts = null;
List<GhostTag>? ghostTags = null;
List<GhostAuthor>? ghostAuthors = null;

var wpTagsDict = new Dictionary<int, string>();
var wpUsersDict = new Dictionary<int, User>();
var ghostTagsDict = new Dictionary<string, string>(); // slug -> ghost id
var ghostAuthorsDict = new Dictionary<string, GhostAuthor>(); // slug -> ghost author

// await LoadLocalData();
await DownloadAndSaveAllData();
// await ProcessPosts(20);
await ProcessPosts(slug: "puzzling-places");
await GenerateRedirects();

// async Task LoadLocalData()
// {
//     if (File.Exists("wpPosts.txt"))
//         wpPosts = JsonConvert.DeserializeObject<List<Post>>(await File.ReadAllTextAsync("wpPosts.txt"));
//     if (File.Exists("wpTags.txt"))
//         wpTags = JsonConvert.DeserializeObject<List<Tag>>(await File.ReadAllTextAsync("wpTags.txt"));
//     if (File.Exists("wpUsers.txt"))
//         wpAuthors = JsonConvert.DeserializeObject<List<User>>(await File.ReadAllTextAsync("wpUsers.txt"));
//     if (File.Exists("wpCategories.txt"))
//         wpCategories = JsonConvert.DeserializeObject<List<Category>>(await File.ReadAllTextAsync("wpCategories.txt"));
//     if (File.Exists("ghostPosts.txt"))
//         ghostPosts = JsonConvert.DeserializeObject<List<GhostPost>>(await File.ReadAllTextAsync("ghostPosts.txt"));
//     if (File.Exists("ghostTags.txt"))
//         ghostTags = JsonConvert.DeserializeObject<List<GhostTag>>(await File.ReadAllTextAsync("ghostTags.txt"));
// }

async Task DownloadAndSaveAllData()
{
    // Load existing data if available
    var existingPostIds = new HashSet<int>();
    var wpPostsFile = Path.Combine(dataFolder, "wpPosts.txt");
    if (File.Exists(wpPostsFile))
    {
        var existingPosts = JsonConvert.DeserializeObject<List<Post>>(await File.ReadAllTextAsync(wpPostsFile));
        if (existingPosts != null)
        {
            wpPosts = existingPosts;
            foreach (var p in existingPosts)
                existingPostIds.Add(p.Id);
            Console.WriteLine($"Loaded {existingPostIds.Count} existing WP posts from cache");
        }
    }

    wpPosts ??= [];

    // First, fetch only IDs to check what's new (minimal data transfer)
    Console.WriteLine("Checking for new WordPress posts...");
    using var http = new HttpClient();
    var page = 1;
    var newPostIds = new List<int>();

    while (true)
    {
        try
        {
            var url = $"https://holographica.space/wp-json/wp/v2/posts?_fields=id&per_page=100&page={page}";
            var response = await http.GetAsync(url);

            if (!response.IsSuccessStatusCode) break; // No more pages

            var json = await response.Content.ReadAsStringAsync();
            var ids = JsonConvert.DeserializeObject<List<IdOnly>>(json) ?? [];

            if (ids.Count == 0) break;

            foreach (var item in ids)
            {
                if (!existingPostIds.Contains(item.Id))
                    newPostIds.Add(item.Id);
            }

            page++;
        }
        catch
        {
            break; // Error fetching page, stop
        }
    }

    Console.WriteLine($"Found {newPostIds.Count} new posts");

    // Download only new posts
    if (newPostIds.Count > 0)
    {
        Console.WriteLine("Downloading new posts...");
        var downloaded = 0;
        foreach (var id in newPostIds)
        {
            downloaded++;
            Console.Write($"\r  Downloading post {downloaded}/{newPostIds.Count}...");
            var post = await wp.Posts.GetByIDAsync(id);
            wpPosts.Add(post);
        }
        Console.WriteLine($"\r  Downloaded {newPostIds.Count} new posts              ");
    }

    wpAuthors = (await wp.Users.GetAllAsync()).ToList();
    wpTags = (await wp.Tags.GetAllAsync()).ToList();
    wpCategories = (await wp.Categories.GetAllAsync()).ToList();

    Console.WriteLine("Downloading Ghost data...");
    ghostPosts = await ghostClient.GetAllPostsAsync();
    ghostTags = await ghostClient.GetAllTagsAsync();
    ghostAuthors = await ghostClient.GetAllAuthorsAsync();

    await File.WriteAllTextAsync(Path.Combine(dataFolder, "wpUsers.txt"), JsonConvert.SerializeObject(wpAuthors));
    await File.WriteAllTextAsync(Path.Combine(dataFolder, "wpTags.txt"), JsonConvert.SerializeObject(wpTags));
    await File.WriteAllTextAsync(wpPostsFile, JsonConvert.SerializeObject(wpPosts));
    await File.WriteAllTextAsync(Path.Combine(dataFolder, "wpCategories.txt"), JsonConvert.SerializeObject(wpCategories));
    await File.WriteAllTextAsync(Path.Combine(dataFolder, "ghostPosts.txt"), JsonConvert.SerializeObject(ghostPosts));
    await File.WriteAllTextAsync(Path.Combine(dataFolder, "ghostTags.txt"), JsonConvert.SerializeObject(ghostTags));
    await File.WriteAllTextAsync(Path.Combine(dataFolder, "ghostAuthors.txt"), JsonConvert.SerializeObject(ghostAuthors));

    Console.WriteLine($"Saved {wpPosts.Count} WP posts, {ghostPosts.Count} Ghost posts, {ghostAuthors.Count} Ghost authors");
}

async Task ProcessPosts(int? count = null, string? slug = null)
{
    BuildLookupDictionaries();
    if (wpPosts == null) return;

    IEnumerable<Post> postsToProcess;

    if (!string.IsNullOrEmpty(slug))
    {
        // Process a specific post by slug
        postsToProcess = wpPosts.Where(p => p.Slug == slug);
    }
    else
    {
        // Process posts not yet in Ghost
        postsToProcess = wpPosts.Where(p => ghostPosts?.Any(g => g.Slug == p.Slug) != true);

        if (count.HasValue)
            postsToProcess = postsToProcess.Take(count.Value);
    }

    var postsList = postsToProcess.ToList();
    var total = postsList.Count;

    if (total == 0)
    {
        Console.WriteLine(slug != null ? $"Post with slug '{slug}' not found" : "No posts to process");
        return;
    }

    Console.WriteLine($"Processing {total} post(s)...");

    var processed = 0;
    foreach (var wpPost in postsList)
    {
        processed++;
        var progress = (processed / (decimal)total * 100).ToString("F2");
        Console.Write($"Progress {progress}% ");

        await MovePostToGhost(wpPost, wpPost.Slug);
    }
}

void BuildLookupDictionaries()
{
    if (wpTags != null)
        foreach (var tag in wpTags)
            wpTagsDict[tag.Id] = tag.Slug;

    if (wpAuthors != null)
        foreach (var user in wpAuthors)
            wpUsersDict[user.Id] = user;

    if (ghostTags != null)
        foreach (var tag in ghostTags)
            ghostTagsDict[tag.Slug] = tag.Id;

    if (ghostAuthors != null)
        foreach (var author in ghostAuthors)
            ghostAuthorsDict[author.Slug] = author;
}

async Task GenerateRedirects()
{
    BuildLookupDictionaries();
    if (wpPosts == null || wpCategories == null) return;

    var redirects = new List<object>();

    foreach (var post in wpPosts)
    {
        var categoryId = post.Categories.FirstOrDefault();
        var category = wpCategories.FirstOrDefault(c => c.Id == categoryId);
        var categorySlug = category?.Slug ?? "uncategorized";

        // WordPress URL: /category/post-slug/
        // Ghost URL: /post-slug/
        redirects.Add(new
        {
            from = $"/{categorySlug}/{post.Slug}/",
            to = $"/{post.Slug}/",
            permanent = true
        });
    }

    var json = JsonConvert.SerializeObject(redirects, Formatting.Indented);
    var redirectsFile = Path.Combine(dataFolder, "redirects.json");
    await File.WriteAllTextAsync(redirectsFile, json);
    Console.WriteLine($"Generated {redirects.Count} redirects to {redirectsFile}");
}

async Task MovePostToGhost(Post wpPost, string targetSlug)
{
    Console.WriteLine($"Raw: {wpPost.Content.Raw} \nRendered: {wpPost.Content.Rendered}");
    Console.WriteLine($"Creating post: {targetSlug}");

    // Convert WP tags to Ghost tags
    var postTags = new List<GhostTag>();
    foreach (var tagId in wpPost.Tags)
    {
        if (!wpTagsDict.TryGetValue(tagId, out var slug)) continue;

        var tagName = wpTags?.FirstOrDefault(t => t.Id == tagId)?.Name ?? slug;

        if (ghostTagsDict.TryGetValue(slug, out var ghostTagId))
        {
            postTags.Add(new GhostTag { Id = ghostTagId, Slug = slug, Name = tagName });
        }
        else
        {
            // Create new tag in Ghost
            var newTag = await ghostClient.CreateTagAsync(slug, tagName);
            if (newTag == null) continue;
            ghostTags?.Add(newTag);
            ghostTagsDict[newTag.Slug] = newTag.Id;
            postTags.Add(newTag);
        }
    }

    // Also convert categories to tags (Ghost doesn't have categories)
    foreach (var category in wpPost.Categories.Select(catId => wpCategories?.FirstOrDefault(c => c.Id == catId)).Where(category => category != null))
    {
        if (ghostTagsDict.TryGetValue(category!.Slug, out var ghostTagId))
        {
            postTags.Add(new GhostTag { Id = ghostTagId, Slug = category.Slug, Name = category.Name });
        }
        else
        {
            var newTag = await ghostClient.CreateTagAsync(category.Slug, category.Name);
            if (newTag == null) continue;
            ghostTags?.Add(newTag);
            ghostTagsDict[newTag.Slug] = newTag.Id;
            postTags.Add(newTag);
        }
    }

    // Process images in content
    var content = wpPost.Content.Rendered;
    string? featureImage = null;
    string? featureImageSrc = null;

    html.LoadHtml(content);
    var imgNodes = html.DocumentNode.SelectNodes("//img");
    foreach (var imgNode in imgNodes)
    {
        var src = imgNode.GetAttributeValue("src", string.Empty);
        if (string.IsNullOrEmpty(src)) continue;

        try
        {
            // Upload image to Ghost
            var uploadedUrl = await ghostClient.UploadImageAsync(src);
            if (string.IsNullOrEmpty(uploadedUrl)) continue;

            // First image becomes featured image
            if (featureImage == null)
            {
                featureImage = uploadedUrl;
                featureImageSrc = src;
            }
            else
            {
                // Update other images in content with new URL
                content = content.Replace(src, uploadedUrl);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to upload image {src}: {ex.Message}");
        }
    }

    // Remove the featured image from content to avoid duplication
    if (featureImageSrc != null)
    {
        html.LoadHtml(content);
        var featureImgNode = html.DocumentNode.SelectSingleNode($"//img[@src='{featureImageSrc}']");
        {
            // Remove the img tag and its parent if it's a figure or empty p/div
            var parent = featureImgNode.ParentNode;
            featureImgNode.Remove();
            if (parent.Name == "figure" || (parent.Name is "p" or "div" && string.IsNullOrWhiteSpace(parent.InnerText)))
            {
                parent.Remove();
            }
            content = html.DocumentNode.OuterHtml;
        }
    }

    // Process file links (PDFs, documents, etc.) stored in WordPress
    content = await ProcessFileLinks(content, ghostClient);

    // Convert TablePress tables to Ghost tables
    content = ProcessTablePressTables(content);

    // Process videos in content
    content = await ProcessVideos(content, ghostClient);

    // Strip HTML tags from excerpt
    var excerpt = wpPost.Excerpt.Rendered;
    if (!string.IsNullOrEmpty(excerpt))
    {
        html.LoadHtml(excerpt);
        excerpt = html.DocumentNode.InnerText.Trim();
    }

    // Map WordPress author to Ghost author
    var postAuthors = new List<GhostAuthor>();
    if (wpUsersDict.TryGetValue(wpPost.Author, out var wpAuthor))
    {
        // Try to find matching Ghost author by slug or name
        var ghostAuthor = ghostAuthorsDict.Values.FirstOrDefault(a =>
            a.Slug.Equals(wpAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
            a.Name.Equals(wpAuthor.Name, StringComparison.OrdinalIgnoreCase));

        if (ghostAuthor != null)
        {
            postAuthors.Add(ghostAuthor);
            Console.WriteLine($"  Mapped author: {wpAuthor.Name} -> {ghostAuthor.Name}");
        }
        else
        {
            Console.WriteLine($"  Warning: No matching Ghost author for WP user '{wpAuthor.Name}' (slug: {wpAuthor.Slug})");
        }
    }


    // DO NOT apply HtmlDecode to entire content - it corrupts HTML comments like <!--kg-card-begin: video-->
    // WordPress HTML entities (&#8217; etc.) in text will be handled by Ghost during import
    // We only decode title and excerpt which are plain text
    var decodedContent = content; // Keep HTML intact

    // Create Ghost post - decode HTML entities in title, content and excerpt
    var ghostPost = new GhostPost
    {
        Title = System.Net.WebUtility.HtmlDecode(wpPost.Title.Rendered),
        Slug = targetSlug,
        Html = decodedContent,
        CustomExcerpt = string.IsNullOrEmpty(excerpt) ? null : System.Net.WebUtility.HtmlDecode(excerpt),
        Status = wpPost.Status == Status.Publish ? "published" : "draft",
        PublishedAt = wpPost.Date,
        Tags = postTags,
        FeatureImage = featureImage,
        Authors = postAuthors.Count > 0 ? postAuthors : null
    };

    var created = await ghostClient.CreatePostAsync(ghostPost);
    if (created != null)
    {
        ghostPosts?.Add(created);
        Console.WriteLine($"Created: {created.Slug}");

    }
}

async Task<string> ProcessVideos(string content, GhostAdminClient client)
{
    var doc = new HtmlDocument();
    doc.LoadHtml(content);
    var videosProcessed = 0;
    var videoReplacements = new Dictionary<string, string>(); // placeholder -> actual HTML with comments

    // Video URL patterns
    const string youtubePattern = @"(?:https?://)?(?:www\.)?(?:youtube\.com/watch\?v=|youtu\.be/|youtube\.com/embed/)([a-zA-Z0-9_-]{11})";
    const string vimeoPattern = @"(?:https?://)?(?:www\.)?(?:vimeo\.com/|player\.vimeo\.com/video/)(\d+)";
    const string videoFilePattern = @"(https?://[^\s<>""']+\.(?:mp4|webm|ogg|mov))";

    // NOTE: We do NOT convert anchor tags (<a href="video">) to embeds - those are just links
    // Only process actual embedded video elements (shortcodes, iframes, video tags) below

    // Process WordPress [video] shortcodes - e.g. [video mp4="url" ...][/video] or [video src="url" .../]
    // WordPress uses format-specific attributes like mp4="...", webm="...", ogg="..." or generic src="..."
    const string videoShortcodePattern = @"\[video\s+[^\]]*(?:mp4|webm|ogg|mov|src)=[""']([^""']+)[""'][^\]]*\](?:\[/video\])?";
    var shortcodeMatches = Regex.Matches(content, videoShortcodePattern, RegexOptions.IgnoreCase);

    // Also process [embed] shortcodes - e.g. [embed]https://youtube.com/...[/embed]
    const string embedShortcodePattern = @"\[embed\]([^\[]+)\[/embed\]";
    var embedMatches = Regex.Matches(content, embedShortcodePattern, RegexOptions.IgnoreCase);
    foreach (Match match in embedMatches)
    {
        var url = match.Groups[1].Value.Trim();

        // Check if it's a YouTube URL
        var youtubeMatch = Regex.Match(url, youtubePattern);
        if (youtubeMatch.Success)
        {
            var videoId = youtubeMatch.Groups[1].Value;
            var embedHtml = $@"<figure class=""kg-card kg-embed-card""><iframe width=""560"" height=""315"" src=""https://www.youtube.com/embed/{videoId}"" frameborder=""0"" allow=""accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"" allowfullscreen></iframe></figure>";
            content = content.Replace(match.Value, embedHtml);
            videosProcessed++;
            continue;
        }

        // Check if it's a Vimeo URL
        var vimeoMatch = Regex.Match(url, vimeoPattern);
        if (vimeoMatch.Success)
        {
            var videoId = vimeoMatch.Groups[1].Value;
            var embedHtml = $@"<figure class=""kg-card kg-embed-card""><iframe src=""https://player.vimeo.com/video/{videoId}"" width=""560"" height=""315"" frameborder=""0"" allow=""autoplay; fullscreen; picture-in-picture"" allowfullscreen></iframe></figure>";
            content = content.Replace(match.Value, embedHtml);
            videosProcessed++;
            continue;
        }

        // Check if it's a direct video file
        var videoMatch = Regex.Match(url, videoFilePattern, RegexOptions.IgnoreCase);
        if (videoMatch.Success)
        {
            var uploadedUrl = await client.UploadVideoAsync(url);
            if (!string.IsNullOrEmpty(uploadedUrl))
            {
                // Use Ghost's HTML card to preserve the video tag
                var embedHtml = $@"<!--kg-card-begin: html-->
<div class=""video-container"" style=""position:relative;padding-bottom:56.25%;height:0;overflow:hidden;max-width:100%"">
<video controls style=""position:absolute;top:0;left:0;width:100%;height:100%"" preload=""metadata"">
<source src=""{uploadedUrl}"" type=""video/mp4"">
Your browser does not support the video tag.
</video>
</div>
<!--kg-card-end: html-->";
                content = content.Replace(match.Value, embedHtml);
                videosProcessed++;
            }
        }
    }
    foreach (Match match in shortcodeMatches)
    {
        var videoUrl = match.Groups[1].Value;
        var uploadedUrl = await client.UploadVideoAsync(videoUrl);
        if (string.IsNullOrEmpty(uploadedUrl)) continue;
        // Use Ghost's HTML card to preserve the video tag
        var embedHtml = $@"<!--kg-card-begin: html-->
<div class=""video-container"" style=""position:relative;padding-bottom:56.25%;height:0;overflow:hidden;max-width:100%"">
<video controls style=""position:absolute;top:0;left:0;width:100%;height:100%"" preload=""metadata"">
<source src=""{uploadedUrl}"" type=""video/mp4"">
Your browser does not support the video tag.
</video>
</div>
<!--kg-card-end: html-->";
        content = content.Replace(match.Value, embedHtml);
        videosProcessed++;
    }

    // Reload doc after shortcode processing
    doc.LoadHtml(content);

    // Process existing <video> tags - upload source to Ghost
    var videoNodes = doc.DocumentNode.SelectNodes("//video");
    if (videoNodes != null)
    foreach (var video in videoNodes.ToList())
    {
        // Get video source - either from src attribute or from <source> child
        var src = video.GetAttributeValue("src", "");
        if (string.IsNullOrEmpty(src))
        {
            var sourceNode = video.SelectSingleNode(".//source[@src]");
            src = sourceNode?.GetAttributeValue("src", "") ?? "";
        }

        if (string.IsNullOrEmpty(src)) continue;

        // Upload to Ghost
        var uploadedUrl = await client.UploadVideoAsync(src);
        if (string.IsNullOrEmpty(uploadedUrl)) continue;

        // Ghost seems to strip video tags from HTML content. Try using an HTML embed card with iframe-like structure.
        // This uses a raw HTML block that Ghost should preserve.
        var placeholder = $"__VIDEO_PLACEHOLDER_{videosProcessed}__";
        // Use an iframe with video - this might be preserved better than raw video tag
        var embedHtml = $@"<!--kg-card-begin: html-->
<div class=""video-container"" style=""position:relative;padding-bottom:56.25%;height:0;overflow:hidden;max-width:100%"">
<video controls style=""position:absolute;top:0;left:0;width:100%;height:100%"" preload=""metadata"">
<source src=""{uploadedUrl}"" type=""video/mp4"">
Your browser does not support the video tag.
</video>
</div>
<!--kg-card-end: html-->";

        // Replace video or its parent figure with a placeholder span
        var placeholderNode = HtmlNode.CreateNode($"<span>{placeholder}</span>");
        var parent = video.ParentNode;
        if (parent.Name == "figure")
        {
            parent.ParentNode.ReplaceChild(placeholderNode, parent);
        }
        else
        {
            parent.ReplaceChild(placeholderNode, video);
        }

        // Store for later replacement in the final HTML string
        videoReplacements[placeholder] = embedHtml;
        videosProcessed++;
    }

    // Process existing iframes (YouTube/Vimeo embeds from WordPress)
    var iframeNodes = doc.DocumentNode.SelectNodes("//iframe[@src]");
    if (iframeNodes != null)
    foreach (var iframe in iframeNodes.ToList())
    {
        var src = iframe.GetAttributeValue("src", "");
        if (string.IsNullOrEmpty(src)) continue;

        // Ensure iframes are wrapped in figure tags for Ghost
        var parent = iframe.ParentNode;
        if (parent.Name == "figure") continue;
        var figureHtml = $"""<figure class="kg-card kg-embed-card">{iframe.OuterHtml}</figure>""";
        var figureNode = HtmlNode.CreateNode(figureHtml);
        parent.ReplaceChild(figureNode, iframe);
        videosProcessed++;
    }

    // NOTE: We do NOT process plain text URLs - if someone wrote a URL as plain text, keep it as-is
    // Only actual embedded elements (iframes, video tags) are processed above

    if (videosProcessed > 0)
        Console.WriteLine($"  Processed {videosProcessed} video(s)");

    // Get the final HTML and replace placeholders with actual video HTML (including comments)
    var result = doc.DocumentNode.OuterHtml;
    foreach (var (placeholder, videoHtml) in videoReplacements)
    {
        result = result.Replace($"<span>{placeholder}</span>", videoHtml);
    }

    return result;
}

async Task<string> ProcessFileLinks(string content, GhostAdminClient client)
{
    var doc = new HtmlDocument();
    doc.LoadHtml(content);
    var filesProcessed = 0;

    // Pattern to match internal WordPress storage URLs
    const string internalStoragePattern = @"https?://holographica\.space/wp-content/uploads/[^\s""'<>]+";

    // Find all anchor tags that link to internal files
    // IMPORTANT: Skip anchors inside <video> elements (they are fallback content, not real links)
    var anchorNodes = doc.DocumentNode.SelectNodes("//a[@href][not(ancestor::video)]");
    if (anchorNodes != null)
    foreach (var anchor in anchorNodes.ToList())
    {
        var href = anchor.GetAttributeValue("href", "");
        if (string.IsNullOrEmpty(href)) continue;

        // Check if this is an internal storage link (not an image - those are handled separately)
        if (Regex.IsMatch(href, internalStoragePattern, RegexOptions.IgnoreCase))
        {
            var ext = Path.GetExtension(href).TrimEnd('?', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '=', '_').ToLower();
            // Skip image files - they're handled by the image processor
            if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".svg")
                continue;
            // Skip video files - they're handled by the video processor
            if (ext is ".mp4" or ".webm" or ".ogg" or ".mov")
                continue;

            try
            {
                var uploadedUrl = await client.UploadFileAsync(href);
                if (!string.IsNullOrEmpty(uploadedUrl))
                {
                    anchor.SetAttributeValue("href", uploadedUrl);
                    filesProcessed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Failed to upload file {href}: {ex.Message}");
            }
        }
    }

    if (filesProcessed > 0)
        Console.WriteLine($"  Processed {filesProcessed} file link(s)");

    return doc.DocumentNode.OuterHtml;
}

string ProcessTablePressTables(string content)
{
    // First, replace any remaining TablePress shortcodes with empty string (they won't render anyway)
    content = Regex.Replace(content, @"\[table\s+id=[^\]]+\s*/?\]", "", RegexOptions.IgnoreCase);
    content = Regex.Replace(content, @"\[tablepress\s+id=[^\]]+\s*/?\]", "", RegexOptions.IgnoreCase);
    content = Regex.Replace(content, @"\[/table\]", "", RegexOptions.IgnoreCase);
    content = Regex.Replace(content, @"\[/tablepress\]", "", RegexOptions.IgnoreCase);

    var doc = new HtmlDocument();
    doc.LoadHtml(content);
    var tablesProcessed = 0;

    // Find all TablePress tables
    var tablePressNodes = doc.DocumentNode.SelectNodes("//table[contains(@class, 'tablepress')]");
    if (tablePressNodes == null)
        return doc.DocumentNode.OuterHtml;

    foreach (var table in tablePressNodes.ToList())
    {
        // Create a clean Ghost-compatible table
        var newTable = doc.CreateElement("table");

        // Process thead if exists
        var thead = table.SelectSingleNode(".//thead");
        if (thead != null)
        {
            var newThead = doc.CreateElement("thead");
            var headerRows = thead.SelectNodes(".//tr");
            if (headerRows != null)
            foreach (var row in headerRows)
            {
                var newRow = doc.CreateElement("tr");
                var cells = row.SelectNodes(".//th|.//td");
                if (cells != null)
                foreach (var cell in cells)
                {
                    var newCell = doc.CreateElement("th");
                    newCell.InnerHtml = cell.InnerHtml.Trim();
                    var colspan = cell.GetAttributeValue("colspan", "");
                    var rowspan = cell.GetAttributeValue("rowspan", "");
                    if (!string.IsNullOrEmpty(colspan)) newCell.SetAttributeValue("colspan", colspan);
                    if (!string.IsNullOrEmpty(rowspan)) newCell.SetAttributeValue("rowspan", rowspan);
                    newRow.AppendChild(newCell);
                }
                newThead.AppendChild(newRow);
            }
            newTable.AppendChild(newThead);
        }

        // Process tbody
        var tbody = table.SelectSingleNode(".//tbody");
        if (tbody != null)
        {
            var newTbody = doc.CreateElement("tbody");
            var bodyRows = tbody.SelectNodes(".//tr");
            if (bodyRows != null)
            foreach (var row in bodyRows)
            {
                var newRow = doc.CreateElement("tr");
                var cells = row.SelectNodes(".//td|.//th");
                if (cells != null)
                foreach (var cell in cells)
                {
                    var newCell = doc.CreateElement("td");
                    newCell.InnerHtml = cell.InnerHtml.Trim();
                    var colspan = cell.GetAttributeValue("colspan", "");
                    var rowspan = cell.GetAttributeValue("rowspan", "");
                    if (!string.IsNullOrEmpty(colspan)) newCell.SetAttributeValue("colspan", colspan);
                    if (!string.IsNullOrEmpty(rowspan)) newCell.SetAttributeValue("rowspan", rowspan);
                    newRow.AppendChild(newCell);
                }
                newTbody.AppendChild(newRow);
            }
            newTable.AppendChild(newTbody);
        }

        // Process tfoot if exists
        var tfoot = table.SelectSingleNode(".//tfoot");
        if (tfoot != null)
        {
            var newTfoot = doc.CreateElement("tfoot");
            var footerRows = tfoot.SelectNodes(".//tr");
            if (footerRows != null)
            foreach (var row in footerRows)
            {
                var newRow = doc.CreateElement("tr");
                var cells = row.SelectNodes(".//td|.//th");
                if (cells != null)
                foreach (var cell in cells)
                {
                    var newCell = doc.CreateElement("td");
                    newCell.InnerHtml = cell.InnerHtml.Trim();
                    var colspan = cell.GetAttributeValue("colspan", "");
                    var rowspan = cell.GetAttributeValue("rowspan", "");
                    if (!string.IsNullOrEmpty(colspan)) newCell.SetAttributeValue("colspan", colspan);
                    if (!string.IsNullOrEmpty(rowspan)) newCell.SetAttributeValue("rowspan", rowspan);
                    newRow.AppendChild(newCell);
                }
                newTfoot.AppendChild(newRow);
            }
            newTable.AppendChild(newTfoot);
        }

        // Find the wrapper div if it exists and replace the whole thing
        var parent = table.ParentNode;
        if (parent != null && parent.Name == "div" &&
            (parent.GetAttributeValue("class", "").Contains("tablepress") ||
             parent.GetAttributeValue("class", "").Contains("tablepress-scroll-wrapper")))
        {
            // Replace the wrapper div with the clean table
            parent.ParentNode?.ReplaceChild(newTable, parent);
        }
        else
        {
            // Just replace the table
            parent?.ReplaceChild(newTable, table);
        }

        tablesProcessed++;
    }

    // Also clean up any empty TablePress wrapper divs that might remain
    var wrapperDivs = doc.DocumentNode.SelectNodes("//div[contains(@class, 'tablepress-scroll-wrapper')]");
    if (wrapperDivs != null)
    foreach (var wrapper in wrapperDivs.ToList())
    {
        // If it just contains a table, unwrap it
        var innerTable = wrapper.SelectSingleNode(".//table");
        if (innerTable != null)
            wrapper.ParentNode?.ReplaceChild(innerTable, wrapper);
    }

    if (tablesProcessed > 0)
        Console.WriteLine($"  Processed {tablesProcessed} TablePress table(s)");

    return doc.DocumentNode.OuterHtml;
}

class IdOnly
{
    [JsonProperty("id")] public int Id { get; set; }
}

// Ghost API client
public class GhostAdminClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _keyId;
    private readonly byte[] _keySecret;

    public GhostAdminClient(string baseUrl, string adminApiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        var parts = adminApiKey.Split(':');
        _keyId = parts[0];
        _keySecret = Convert.FromHexString(parts[1]);
    }

    private string GenerateToken()
    {
        var now = DateTime.UtcNow;
        var key = new SymmetricSecurityKey(_keySecret) { KeyId = _keyId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var header = new JwtHeader(credentials)
        {
            ["kid"] = _keyId
        };

        var payload = new JwtPayload(
            issuer: null,
            audience: "/admin/",
            claims: null,
            notBefore: null,
            expires: now.AddMinutes(5),
            issuedAt: now);

        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, $"{_baseUrl}/ghost/api/admin/{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Ghost", GenerateToken());
        return request;
    }

    public async Task<List<GhostPost>> GetAllPostsAsync()
    {
        var posts = new List<GhostPost>();
        var page = 1;

        while (true)
        {
            var request = CreateRequest(HttpMethod.Get, $"posts/?limit=100&page={page}");
            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error getting posts: {json}");
                break;
            }

            var result = JObject.Parse(json);
            var pagePosts = result["posts"]?.ToObject<List<GhostPost>>() ?? [];

            if (pagePosts.Count == 0) break;

            posts.AddRange(pagePosts);
            page++;
        }

        return posts;
    }

    public async Task<List<GhostTag>> GetAllTagsAsync()
    {
        var request = CreateRequest(HttpMethod.Get, "tags/?limit=all");
        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error getting tags: {json}");
            return [];
        }

        var result = JObject.Parse(json);
        return result["tags"]?.ToObject<List<GhostTag>>() ?? [];
    }

    public async Task<GhostTag?> CreateTagAsync(string slug, string name)
    {
        var request = CreateRequest(HttpMethod.Post, "tags/");
        var payload = new { tags = new[] { new { slug, name } } };
        request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error creating tag {slug}: {json}");
            return null;
        }

        var result = JObject.Parse(json);
        return result["tags"]?[0]?.ToObject<GhostTag>();
    }

    public async Task<List<GhostAuthor>> GetAllAuthorsAsync()
    {
        var request = CreateRequest(HttpMethod.Get, "users/?limit=all");
        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error getting authors: {json}");
            return [];
        }

        var result = JObject.Parse(json);
        return result["users"]?.ToObject<List<GhostAuthor>>() ?? [];
    }

    public async Task<GhostPost?> CreatePostAsync(GhostPost post)
    {
        var request = CreateRequest(HttpMethod.Post, "posts/?source=html");

        // Truncate excerpt to 300 chars max
        var excerpt = post.CustomExcerpt;
        if (excerpt?.Length > 300)
            excerpt = excerpt[..297] + "...";

        var postData = new Dictionary<string, object?>
        {
            ["title"] = post.Title,
            ["slug"] = post.Slug,
            ["html"] = post.Html,
            ["status"] = post.Status,
            ["custom_excerpt"] = excerpt,
            ["published_at"] = post.PublishedAt?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["feature_image"] = post.FeatureImage,
            ["tags"] = post.Tags?.Select(t => new { id = t.Id, slug = t.Slug, name = t.Name }).ToArray(),
            ["authors"] = post.Authors?.Select(a => new { id = a.Id }).ToArray()
        };

        var payload = new { posts = new[] { postData } };
        request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error creating post {post.Slug}: {json}");
            return null;
        }

        var result = JObject.Parse(json);
        return result["posts"]?[0]?.ToObject<GhostPost>();
    }

    [SuppressMessage("ReSharper", "PossibleLossOfFraction")]
    public async Task<string?> UploadImageAsync(string imageUrl)
    {
        try
        {
            var fileName = Path.GetFileName(new Uri(imageUrl).LocalPath);
            fileName = Regex.Replace(fileName, @"[^\u0000-\u007F]+", "");
            if (fileName.Length > 100) fileName = fileName[..100] + Path.GetExtension(fileName);

            // Download image with progress
            Console.Write($"  Downloading: {fileName}");
            var downloadStart = DateTime.Now;

            using var downloadResponse = await _http.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead);
            var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
            Console.Write($" ({totalBytes / 1024}KB)");

            await using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();
            var buffer = new byte[81920];
            int bytesRead;
            long totalRead = 0;

            while ((bytesRead = await downloadStream.ReadAsync(buffer)) > 0)
            {
                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                if (totalBytes <= 0) continue;
                var percent = (int)(totalRead * 100 / totalBytes);
                var elapsed = (DateTime.Now - downloadStart).TotalSeconds;
                var speed = elapsed > 0 ? totalRead / 1024 / elapsed : 0;
                Console.Write($"\r  Downloading: {fileName} ({totalBytes / 1024}KB) {percent}% @ {speed:F0}KB/s    ");
            }

            var downloadTime = (DateTime.Now - downloadStart).TotalSeconds;
            var downloadSpeed = downloadTime > 0 ? totalRead / 1024 / downloadTime : 0;
            Console.WriteLine($"\r  Downloaded: {fileName} ({totalRead / 1024}KB) @ {downloadSpeed:F0}KB/s              ");

            var imageBytes = memoryStream.ToArray();

            // Upload to Ghost with progress
            Console.Write($"  Uploading: {fileName} ({imageBytes.Length / 1024}KB)");
            var uploadStart = DateTime.Now;

            var request = CreateRequest(HttpMethod.Post, "images/upload/");

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
            content.Add(fileContent, "file", fileName);

            request.Content = content;

            var response = await _http.SendAsync(request);
            var uploadTime = (DateTime.Now - uploadStart).TotalSeconds;
            var uploadSpeed = uploadTime > 0 ? imageBytes.Length / 1024 / uploadTime : 0;

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"\r  Upload failed: {fileName} - {json}                    ");
                return null;
            }

            var result = JObject.Parse(json);
            var uploadedUrl = result["images"]?[0]?["url"]?.ToString();
            Console.WriteLine($"\r  Uploaded: {fileName} ({imageBytes.Length / 1024}KB) @ {uploadSpeed:F0}KB/s              ");
            return uploadedUrl;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  Error: {ex.Message}");
            return null;
        }
    }

    private static string GetMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLower();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".zip" => "application/zip",
            ".rar" => "application/vnd.rar",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".ogg" => "video/ogg",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream"
        };
    }

    [SuppressMessage("ReSharper", "PossibleLossOfFraction")]
    public async Task<string?> UploadVideoAsync(string videoUrl)
    {
        try
        {
            var fileName = Path.GetFileName(new Uri(videoUrl).LocalPath);
            fileName = Regex.Replace(fileName, @"[^\u0000-\u007F]+", "");
            if (fileName.Length > 100) fileName = fileName[..100] + Path.GetExtension(fileName);

            // Download video with progress
            Console.Write($"  Downloading video: {fileName}");
            var downloadStart = DateTime.Now;

            using var downloadResponse = await _http.GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead);
            var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
            Console.Write($" ({totalBytes / 1024}KB)");

            await using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();
            var buffer = new byte[81920];
            int bytesRead;
            long totalRead = 0;

            while ((bytesRead = await downloadStream.ReadAsync(buffer)) > 0)
            {
                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                if (totalBytes > 0)
                {
                    var percent = (int)(totalRead * 100 / totalBytes);
                    var elapsed = (DateTime.Now - downloadStart).TotalSeconds;
                    var speed = elapsed > 0 ? totalRead / 1024 / elapsed : 0;
                    Console.Write($"\r  Downloading video: {fileName} ({totalBytes / 1024}KB) {percent}% @ {speed:F0}KB/s    ");
                }
            }

            var downloadTime = (DateTime.Now - downloadStart).TotalSeconds;
            var downloadSpeed = downloadTime > 0 ? totalRead / 1024 / downloadTime : 0;
            Console.WriteLine($"\r  Downloaded video: {fileName} ({totalRead / 1024}KB) @ {downloadSpeed:F0}KB/s              ");

            var videoBytes = memoryStream.ToArray();

            // Upload to Ghost media endpoint
            Console.Write($"  Uploading video: {fileName} ({videoBytes.Length / 1024}KB)");
            var uploadStart = DateTime.Now;

            var request = CreateRequest(HttpMethod.Post, "media/upload/");

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(videoBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
            content.Add(fileContent, "file", fileName);

            request.Content = content;

            var response = await _http.SendAsync(request);
            var uploadTime = (DateTime.Now - uploadStart).TotalSeconds;
            var uploadSpeed = uploadTime > 0 ? videoBytes.Length / 1024 / uploadTime : 0;

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"\r  Upload failed: {fileName} - {json}                    ");
                return null;
            }

            var result = JObject.Parse(json);
            var uploadedUrl = result["media"]?[0]?["url"]?.ToString();
            Console.WriteLine($"\r  Uploaded video: {fileName} ({videoBytes.Length / 1024}KB) @ {uploadSpeed:F0}KB/s              ");
            return uploadedUrl;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  Error uploading video: {ex.Message}");
            return null;
        }
    }

    [SuppressMessage("ReSharper", "PossibleLossOfFraction")]
    public async Task<string?> UploadFileAsync(string fileUrl)
    {
        try
        {
            var fileName = Path.GetFileName(new Uri(fileUrl).LocalPath);
            fileName = Regex.Replace(fileName, @"[^\u0000-\u007F]+", "");
            if (fileName.Length > 100) fileName = fileName[..100] + Path.GetExtension(fileName);

            // Download file with progress
            Console.Write($"  Downloading file: {fileName}");
            var downloadStart = DateTime.Now;

            using var downloadResponse = await _http.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);
            var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
            Console.Write($" ({totalBytes / 1024}KB)");

            await using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();
            var buffer = new byte[81920];
            int bytesRead;
            long totalRead = 0;

            while ((bytesRead = await downloadStream.ReadAsync(buffer)) > 0)
            {
                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                if (totalBytes > 0)
                {
                    var percent = (int)(totalRead * 100 / totalBytes);
                    var elapsed = (DateTime.Now - downloadStart).TotalSeconds;
                    var speed = elapsed > 0 ? totalRead / 1024 / elapsed : 0;
                    Console.Write($"\r  Downloading file: {fileName} ({totalBytes / 1024}KB) {percent}% @ {speed:F0}KB/s    ");
                }
            }

            var downloadTime = (DateTime.Now - downloadStart).TotalSeconds;
            var downloadSpeed = downloadTime > 0 ? totalRead / 1024 / downloadTime : 0;
            Console.WriteLine($"\r  Downloaded file: {fileName} ({totalRead / 1024}KB) @ {downloadSpeed:F0}KB/s              ");

            var fileBytes = memoryStream.ToArray();

            // Upload to Ghost with progress
            Console.Write($"  Uploading file: {fileName} ({fileBytes.Length / 1024}KB)");
            var uploadStart = DateTime.Now;

            var request = CreateRequest(HttpMethod.Post, "files/upload/");

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
            content.Add(fileContent, "file", fileName);

            request.Content = content;

            var response = await _http.SendAsync(request);
            var uploadTime = (DateTime.Now - uploadStart).TotalSeconds;
            var uploadSpeed = uploadTime > 0 ? fileBytes.Length / 1024 / uploadTime : 0;

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"\r  Upload failed: {fileName} - {json}                    ");
                return null;
            }

            var result = JObject.Parse(json);
            var uploadedUrl = result["files"]?[0]?["url"]?.ToString();
            Console.WriteLine($"\r  Uploaded file: {fileName} ({fileBytes.Length / 1024}KB) @ {uploadSpeed:F0}KB/s              ");
            return uploadedUrl;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  Error uploading file: {ex.Message}");
            return null;
        }
    }
}

// Ghost models
public class GhostPost
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("title")] public string Title { get; set; } = "";
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("html")] public string? Html { get; set; }
    [JsonProperty("custom_excerpt")] public string? CustomExcerpt { get; set; }
    [JsonProperty("status")] public string Status { get; set; } = "draft";
    [JsonProperty("published_at")] public DateTime? PublishedAt { get; set; }
    [JsonProperty("feature_image")] public string? FeatureImage { get; set; }
    [JsonProperty("tags")] public List<GhostTag>? Tags { get; set; }
    [JsonProperty("authors")] public List<GhostAuthor>? Authors { get; set; }
}

public class GhostTag
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
}

public class GhostAuthor
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("email")] public string? Email { get; set; }
}