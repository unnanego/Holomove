using System.Net;
using AngleSharp.Html.Parser;
using Newtonsoft.Json;

namespace Holomove;

public partial class WpMigrator
{
    private string _backupRoot = "";

    private void InitBackup()
    {
        _backupRoot = Path.GetFullPath(_config.BackupPath);
        Directory.CreateDirectory(_backupRoot);
        Directory.CreateDirectory(Path.Combine(_backupRoot, "posts"));
        Directory.CreateDirectory(Path.Combine(_backupRoot, "authors"));
    }

    private string GetPostBackupFolder(WpPost post)
    {
        var date = post.Date;
        return Path.Combine(_backupRoot, "posts",
            date.Year.ToString(),
            date.Month.ToString("D2"),
            date.Day.ToString("D2"),
            post.Slug);
    }

    private bool PostExistsInBackup(WpPost post)
    {
        var folder = GetPostBackupFolder(post);
        return File.Exists(Path.Combine(folder, "post.json"));
    }

    private async Task SavePostToBackup(WpPost post)
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

        // Get featured media URL — prefer in-memory index (no extra API call)
        string? featuredUrl = null;
        if (post.FeaturedMedia > 0)
        {
            if (!_sourceMediaById.TryGetValue(post.FeaturedMedia, out featuredUrl))
                featuredUrl = await GetMediaUrl(post.FeaturedMedia);
        }

        // Populate in-memory lookup so creation/update within same run can find featured image
        if (!string.IsNullOrEmpty(featuredUrl))
            _backupFeaturedMediaUrls[post.Slug] = featuredUrl;
        if (mediaUrls.Count > 0)
            _backupMediaUrls[post.Slug] = mediaUrls;

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
            Status = post.Status.ToLowerInvariant(),
            AuthorName = authorName,
            TagNames = tagNames,
            CategoryNames = categoryNames,
            FeaturedMediaUrl = featuredUrl,
            MediaUrls = mediaUrls,
            Meta = post.Meta ?? await GetPostMeta(post.Id)
        };

        var json = JsonConvert.SerializeObject(backupPost, Formatting.Indented);
        await File.WriteAllTextAsync(Path.Combine(folder, "post.json"), json);

        // Download media files in parallel
        var mediaFolder = Path.Combine(folder, "media");
        var allMediaUrls = new List<string>(mediaUrls);
        if (featuredUrl != null) allMediaUrls.Add(featuredUrl);

        var distinctUrls = allMediaUrls.Distinct().ToList();
        if (distinctUrls.Count > 0)
        {
            await Parallel.ForEachAsync(distinctUrls, new ParallelOptions { MaxDegreeOfParallelism = 5 },
                async (url, _) => await DownloadMediaToDisk(url, mediaFolder));
        }

        CleanupBackupMedia(mediaFolder, distinctUrls);
    }

    private static void CleanupBackupMedia(string mediaFolder, List<string> expectedUrls)
    {
        if (!Directory.Exists(mediaFolder)) return;

        var expectedFilenames = expectedUrls
            .Select(url => Path.GetFileName(new Uri(url).LocalPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(mediaFolder))
        {
            if (!expectedFilenames.Contains(Path.GetFileName(file)))
                File.Delete(file);
        }

        if (Directory.GetFiles(mediaFolder).Length == 0)
            Directory.Delete(mediaFolder);
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
            if (!response.IsSuccessStatusCode)
            {
                _uploadErrors.Add($"{fileName}: backup download HTTP {(int)response.StatusCode} from {sourceUrl}");
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(filePath);
            await stream.CopyToAsync(file);
        }
        catch (Exception ex)
        {
            _uploadErrors.Add($"{Path.GetFileName(new Uri(sourceUrl).LocalPath)}: backup download failed — {ex.Message}");
            // Skip failed media downloads silently for backup
        }
    }

    private async Task SyncAllBackupMedia()
    {
        var postsBySlug = _sourcePosts.ToDictionary(p => p.Slug, p => p);

        // Collect all (url, mediaFolder) pairs that need downloading
        var slugsWithMedia = new HashSet<string>(_backupMediaUrls.Keys);
        foreach (var slug in _backupFeaturedMediaUrls.Keys)
            slugsWithMedia.Add(slug);

        var toDownload = new List<(string Url, string Folder)>();
        foreach (var slug in slugsWithMedia)
        {
            if (!postsBySlug.TryGetValue(slug, out var post)) continue;
            var mediaFolder = Path.Combine(GetPostBackupFolder(post), "media");

            var allUrls = new List<string>();
            if (_backupMediaUrls.TryGetValue(slug, out var mediaUrls))
                allUrls.AddRange(mediaUrls);
            if (_backupFeaturedMediaUrls.TryGetValue(slug, out var featuredUrl))
                allUrls.Add(featuredUrl);

            foreach (var url in allUrls.Distinct())
            {
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (!File.Exists(Path.Combine(mediaFolder, fileName)))
                    toDownload.Add((url, mediaFolder));
            }
        }

        if (toDownload.Count == 0) return;

        var downloaded = 0;
        await Parallel.ForEachAsync(toDownload, new ParallelOptions { MaxDegreeOfParallelism = 5 },
            async (item, _) =>
            {
                await DownloadMediaToDisk(item.Url, item.Folder);
                var fileName = Path.GetFileName(new Uri(item.Url).LocalPath);
                if (File.Exists(Path.Combine(item.Folder, fileName)))
                    Interlocked.Increment(ref downloaded);
            });

        if (downloaded > 0)
            Console.WriteLine($"  Downloaded {downloaded} missing media file(s) to backup.");
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

    private void LoadSourcePostsFromBackup()
    {
        var postsDir = Path.Combine(_backupRoot, "posts");
        if (!Directory.Exists(postsDir)) return;

        var postFiles = Directory.GetFiles(postsDir, "post.json", SearchOption.AllDirectories);
        if (postFiles.Length == 0) return;

        Console.Write($"  Loading {postFiles.Length} posts from backup...");

        var loadedSlugs = _sourcePosts.Select(p => p.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in postFiles)
        {
            try
            {
                var json = File.ReadAllText(file);
                var backup = JsonConvert.DeserializeObject<BackupPost>(json);
                if (backup == null) continue;

                if (!string.IsNullOrEmpty(backup.FeaturedMediaUrl))
                    _backupFeaturedMediaUrls[backup.Slug] = backup.FeaturedMediaUrl;
                if (backup.MediaUrls.Count > 0)
                    _backupMediaUrls[backup.Slug] = backup.MediaUrls;

                if (!loadedSlugs.Add(backup.Slug)) continue;

                _sourcePosts.Add(new WpPost
                {
                    Slug = backup.Slug,
                    Date = backup.Date,
                    Title = new WpRendered { Rendered = backup.Title },
                    Content = new WpRendered { Rendered = backup.Content },
                    Excerpt = new WpRendered { Rendered = backup.Excerpt },
                    Status = backup.Status.ToLowerInvariant()
                });
            }
            catch { /* skip corrupt files */ }
        }

        Console.WriteLine($"\r  Loaded {_sourcePosts.Count} posts from backup.                    ");
    }

    private async Task SaveTargetPostCache()
    {
        var cache = _targetPosts.Where(p => p.Id > 0).Select(p => new
        {
            p.Id, p.Slug, p.Author, p.Tags, p.Categories, p.FeaturedMedia
        }).ToList();
        await File.WriteAllTextAsync(
            Path.Combine(_backupRoot, "target-cache.json"),
            JsonConvert.SerializeObject(cache));
    }

    private void LoadTargetPostCache()
    {
        var path = Path.Combine(_backupRoot, "target-cache.json");
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var cache = JsonConvert.DeserializeObject<List<WpPost>>(json) ?? [];
            Console.WriteLine($"  Loaded {cache.Count} target posts from cache.");

            foreach (var item in cache)
            {
                if (_targetPosts.Any(p => p.Id == item.Id)) continue;
                _targetPosts.Add(item);
            }
        }
        catch { /* ignore corrupt cache */ }
    }

    private void LoadVerifiedPostsCache()
    {
        var path = Path.Combine(_backupRoot, "verified-posts.json");
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var slugs = JsonConvert.DeserializeObject<List<string>>(json) ?? [];
            foreach (var slug in slugs)
                _verifiedPosts[slug] = true;
            Console.WriteLine($"  Loaded {slugs.Count} verified posts from cache.");
        }
        catch { /* ignore corrupt cache */ }
    }

    private void SaveVerifiedPostsCache()
    {
        var slugs = _verifiedPosts.Keys.OrderBy(s => s).ToList();
        File.WriteAllText(
            Path.Combine(_backupRoot, "verified-posts.json"),
            JsonConvert.SerializeObject(slugs));
    }

    private async Task SaveMetadataToBackup()
    {
        var metadata = new BackupMetadata
        {
            SiteUrl = _config.SourceWpUrl,
            ExportDate = DateTime.Now,
            PostCount = _sourcePosts.Count,
            AuthorCount = _sourceAuthors.Count,
            TagCount = _sourceTags.Count,
            CategoryCount = _sourceCategories.Count
        };
        
        await File.WriteAllTextAsync(Path.Combine(_backupRoot, "metadata.json"), JsonConvert.SerializeObject(metadata, Formatting.Indented));
    }
}
