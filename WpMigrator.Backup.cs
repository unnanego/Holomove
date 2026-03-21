using System.Net;
using AngleSharp.Html.Parser;
using Newtonsoft.Json;
using WordPressPCL.Models;

namespace Holomove;

public partial class WpMigrator
{
    private string _backupRoot = Config.BackupPath;

    private void InitBackup()
    {
        _backupRoot = Path.GetFullPath(Config.BackupPath);
        Directory.CreateDirectory(_backupRoot);
        Directory.CreateDirectory(Path.Combine(_backupRoot, "posts"));
        Directory.CreateDirectory(Path.Combine(_backupRoot, "authors"));
    }

    private string GetPostBackupFolder(Post post)
    {
        var date = post.Date;
        return Path.Combine(_backupRoot, "posts",
            date.Year.ToString(),
            date.Month.ToString("D2"),
            date.Day.ToString("D2"),
            post.Slug);
    }

    private bool PostExistsInBackup(Post post)
    {
        var folder = GetPostBackupFolder(post);
        return File.Exists(Path.Combine(folder, "post.json"));
    }

    private async Task SavePostToBackup(Post post)
    {
        var folder = GetPostBackupFolder(post);
        Directory.CreateDirectory(folder);

        // Resolve human-readable names
        var authorName = _sourceUsersDict.TryGetValue(post.Author, out var author) ? author.Name : "unknown";

        var tagNames = post.Tags
            .Where(id => _sourceTagsDict.ContainsKey(id))
            .Select(id => _sourceTagsDict[id].Name)
            .ToList();

        var categoryNames = post.Categories
            .Where(id => _sourceCategoriesDict.ContainsKey(id))
            .Select(id => _sourceCategoriesDict[id].Name)
            .ToList();

        // Extract media URLs from content
        var mediaUrls = ExtractMediaUrls(post.Content.Rendered)
            .Where(IsSourceMedia)
            .ToList();

        // Get featured media URL
        string? featuredUrl = null;
        if (post.FeaturedMedia > 0)
            featuredUrl = await GetMediaUrl(post.FeaturedMedia.Value);

        // Prepare excerpt
        var excerpt = post.Excerpt.Rendered;
        if (!string.IsNullOrEmpty(excerpt))
        {
            var parser = new HtmlParser();
            var doc = parser.ParseDocument($"<body>{excerpt}</body>");
            excerpt = WebUtility.HtmlDecode(doc.Body?.TextContent.Trim() ?? "");
        }

        var backupPost = new BackupPost
        {
            Title = WebUtility.HtmlDecode(post.Title.Rendered),
            Slug = post.Slug,
            Content = post.Content.Rendered,
            Excerpt = excerpt ?? "",
            Date = post.Date,
            Status = post.Status.ToString(),
            AuthorName = authorName,
            TagNames = tagNames,
            CategoryNames = categoryNames,
            FeaturedMediaUrl = featuredUrl,
            MediaUrls = mediaUrls,
            Meta = await GetPostMeta(post.Id)
        };

        var json = JsonConvert.SerializeObject(backupPost, Formatting.Indented);
        await File.WriteAllTextAsync(Path.Combine(folder, "post.json"), json);

        // Download media files
        var mediaFolder = Path.Combine(folder, "media");
        var allMediaUrls = new List<string>(mediaUrls);
        if (featuredUrl != null) allMediaUrls.Add(featuredUrl);

        foreach (var url in allMediaUrls.Distinct())
        {
            await DownloadMediaToDisk(url, mediaFolder);
        }
    }

    private async Task DownloadMediaToDisk(string sourceUrl, string targetFolder)
    {
        try
        {
            var uri = new Uri(sourceUrl);
            var fileName = Path.GetFileName(uri.LocalPath);
            var filePath = Path.Combine(targetFolder, fileName);

            if (File.Exists(filePath)) return; // Skip if already downloaded

            Directory.CreateDirectory(targetFolder);

            using var response = await _httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return;

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(filePath);
            await stream.CopyToAsync(file);
        }
        catch
        {
            // Skip failed media downloads silently for backup
        }
    }

    private async Task SaveAuthorsToBackup()
    {
        foreach (var author in _sourceAuthors)
        {
            var backup = new BackupAuthor
            {
                Name = author.Name,
                Slug = author.Slug,
                Description = author.Description
            };
            var json = JsonConvert.SerializeObject(backup, Formatting.Indented);
            await File.WriteAllTextAsync(Path.Combine(_backupRoot, "authors", $"{author.Slug}.json"), json);
        }
    }

    private async Task SaveTaxonomyToBackup()
    {
        var tags = _sourceTags.Select(t => new { t.Name, t.Slug, t.Description }).ToList();
        await File.WriteAllTextAsync(Path.Combine(_backupRoot, "tags.json"), JsonConvert.SerializeObject(tags, Formatting.Indented));

        var categories = _sourceCategories.Select(c => new { c.Name, c.Slug, c.Description }).ToList();
        await File.WriteAllTextAsync(Path.Combine(_backupRoot, "categories.json"), JsonConvert.SerializeObject(categories, Formatting.Indented));
    }

    private async Task SaveMetadataToBackup()
    {
        var metadata = new BackupMetadata
        {
            SiteUrl = Config.SourceWpUrl,
            ExportDate = DateTime.Now,
            PostCount = _sourcePosts.Count,
            AuthorCount = _sourceAuthors.Count,
            TagCount = _sourceTags.Count,
            CategoryCount = _sourceCategories.Count
        };
        
        await File.WriteAllTextAsync(Path.Combine(_backupRoot, "metadata.json"), JsonConvert.SerializeObject(metadata, Formatting.Indented));
    }
}
