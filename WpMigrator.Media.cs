using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Holomove;

public partial class WpMigrator
{
    private async Task DownloadTargetMediaList()
    {
        LoadTargetMediaCache();

        var existingIds = _targetMediaByUrl.Values.Select(m => m.Id).ToHashSet();
        var page = 1;
        var fetched = 0;

        Console.Write("  Checking for new media on target...");

        while (true)
        {
            try
            {
                var url = $"{_config.TargetWpApiUrl}wp/v2/media?per_page=100&page={page}&_fields=id,source_url";
                var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        break;
                    Console.WriteLine($"\n  Error fetching media page {page}: {response.StatusCode}");
                    break;
                }

                var json = await response.Content.ReadAsStringAsync();
                var mediaItems = JsonConvert.DeserializeObject<List<MediaItem>>(json) ?? [];

                if (mediaItems.Count == 0) break;

                var newOnPage = 0;
                foreach (var media in mediaItems)
                {
                    if (existingIds.Contains(media.Id)) continue;

                    if (!string.IsNullOrEmpty(media.SourceUrl))
                    {
                        _targetMediaByUrl[media.SourceUrl] = media;

                        var filename = Path.GetFileName(new Uri(media.SourceUrl).LocalPath);
                        if (!string.IsNullOrEmpty(filename))
                            _targetMediaByFilename[filename] = media;
                    }
                    existingIds.Add(media.Id);
                    fetched++;
                    newOnPage++;
                }

                if (newOnPage == 0) break;
                page++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  Error on media page {page}: {ex.Message}");
                break;
            }
        }

        Console.WriteLine($"\r  Target media: {_targetMediaByUrl.Count} items ({fetched} new)                    ");
        if (fetched > 0) SaveTargetMediaCache();
    }

    private void LoadTargetMediaCache()
    {
        var path = Path.Combine(_backupRoot, "target-media-cache.json");
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var cache = JsonConvert.DeserializeObject<List<MediaItem>>(json) ?? [];

            foreach (var media in cache)
            {
                if (string.IsNullOrEmpty(media.SourceUrl)) continue;
                _targetMediaByUrl[media.SourceUrl] = media;

                var filename = Path.GetFileName(new Uri(media.SourceUrl).LocalPath);
                if (!string.IsNullOrEmpty(filename))
                    _targetMediaByFilename[filename] = media;
            }

            Console.WriteLine($"  Loaded {cache.Count} target media items from cache.");
        }
        catch { /* ignore corrupt cache */ }
    }

    private void SaveTargetMediaCache()
    {
        var cache = _targetMediaByUrl.Values.ToList();
        File.WriteAllText(
            Path.Combine(_backupRoot, "target-media-cache.json"),
            JsonConvert.SerializeObject(cache));
    }

    /// <summary>
    /// Uploads all media from source content to target (so files exist there).
    /// Does NOT modify content — URLs stay as source domain.
    /// Returns list of uploaded media IDs for post attachment.
    /// </summary>
    private async Task<List<int>> UploadAllPostMedia(string content, int? attachToPostId = null)
    {
        var mediaUrls = ExtractMediaUrls(content).Where(IsSourceMedia).ToList();
        if (mediaUrls.Count == 0) return [];

        var uploadedIds = new System.Collections.Concurrent.ConcurrentBag<int>();

        await Parallel.ForEachAsync(mediaUrls, new ParallelOptions { MaxDegreeOfParallelism = 3 },
            async (url, _) =>
            {
                var uploaded = await UploadMedia(url, attachToPostId);
                if (uploaded != null)
                    uploadedIds.Add(uploaded.Id);
            });

        return uploadedIds.ToList();
    }

    private bool IsSourceMedia(string url)
    {
        return url.Contains(_config.SourceWpUrl) || url.Contains($"{_config.SourceDomain}/wp-content/uploads");
    }

    private async Task<string?> GetMediaUrl(int mediaId)
    {
        try
        {
            var url = $"{_config.SourceWpApiUrl}wp/v2/media/{mediaId}?_fields=source_url";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var result = JObject.Parse(json);
            return result["source_url"]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    [SuppressMessage("ReSharper", "PossibleLossOfFraction")]
    private async Task<MediaItem?> UploadMedia(string sourceUrl, int? attachToPostId = null)
    {
        try
        {
            var uri = new Uri(sourceUrl);
            var fileName = Path.GetFileName(uri.LocalPath);
            var originalFileName = fileName;
            fileName = NonAsciiRegex().Replace(fileName, "");
            if (fileName.Length > 100)
            {
                var ext = Path.GetExtension(fileName);
                fileName = fileName[..(100 - ext.Length)] + ext;
            }

            var targetUrl = sourceUrl.Replace(_config.SourceWpUrl, _config.TargetWpUrl);

            MediaItem? existingMedia = null;
            if (_targetMediaByUrl.TryGetValue(targetUrl, out var byUrl))
                existingMedia = byUrl;
            else if (_targetMediaByFilename.TryGetValue(originalFileName, out var byFilename))
                existingMedia = byFilename;
            else if (_targetMediaByFilename.TryGetValue(fileName, out var bySanitizedFilename))
                existingMedia = bySanitizedFilename;

            if (existingMedia != null)
            {
                if (attachToPostId.HasValue && existingMedia.Id > 0)
                    await AttachMediaToPost(existingMedia.Id, attachToPostId.Value);
                return existingMedia;
            }

            // Only upload from backup — never directly from source
            var backupFile = FindInBackup(originalFileName);
            if (backupFile == null) return null;

            var mediaBytes = await File.ReadAllBytesAsync(backupFile);

            var uploadUrl = $"{_config.TargetWpApiUrl}wp/v2/media";
            if (attachToPostId.HasValue)
                uploadUrl += $"?post={attachToPostId.Value}";

            var request = CreateAuthenticatedRequest(HttpMethod.Post, uploadUrl);

            using var uploadContent = new ByteArrayContent(mediaBytes);
            uploadContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
            uploadContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = fileName
            };
            request.Content = uploadContent;

            var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode) return null;

            var result = JsonConvert.DeserializeObject<MediaItem>(json);

            if (result == null || string.IsNullOrEmpty(result.SourceUrl)) return result;
            _targetMediaByUrl[result.SourceUrl] = result;
            _targetMediaByFilename[fileName] = result;

            return result;
        }
        catch
        {
            return null;
        }
    }

    private async Task AttachMediaToPost(int mediaId, int postId)
    {
        try
        {
            await PostJsonAsync($"{_config.TargetWpApiUrl}wp/v2/media/{mediaId}", new { post = postId });
        }
        catch { /* best effort */ }
    }

    private string? FindInBackup(string fileName)
    {
        if (string.IsNullOrEmpty(_backupRoot)) return null;

        var postsDir = Path.Combine(_backupRoot, "posts");
        if (!Directory.Exists(postsDir)) return null;

        var matches = Directory.GetFiles(postsDir, fileName, SearchOption.AllDirectories);
        return matches.FirstOrDefault();
    }

    private MediaItem? FindMediaOnTarget(string sourceUrl)
    {
        var uri = new Uri(sourceUrl);
        var fileName = Path.GetFileName(uri.LocalPath);
        var sanitized = NonAsciiRegex().Replace(fileName, "");
        var targetUrl = sourceUrl.Replace(_config.SourceWpUrl, _config.TargetWpUrl);

        if (_targetMediaByUrl.TryGetValue(targetUrl, out var byUrl)) return byUrl;
        if (_targetMediaByFilename.TryGetValue(fileName, out var byName)) return byName;
        if (_targetMediaByFilename.TryGetValue(sanitized, out var bySanitized)) return bySanitized;
        return null;
    }

    private List<string> FindMissingMedia(string slug)
    {
        if (!_backupMediaUrls.TryGetValue(slug, out var urls))
            return [];

        return urls.Where(url =>
            {
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                var sanitized = NonAsciiRegex().Replace(fileName, "");
                var targetUrl = url.Replace(_config.SourceWpUrl, _config.TargetWpUrl);

                return !_targetMediaByUrl.ContainsKey(targetUrl) &&
                       !_targetMediaByFilename.ContainsKey(fileName) &&
                       !_targetMediaByFilename.ContainsKey(sanitized);
            })
            .ToList();
    }

    [GeneratedRegex(@"[^\u0000-\u007F]+")]
    private static partial Regex NonAsciiRegex();

    private static List<string> ExtractMediaUrls(string content)
    {
        var urls = new List<string>();
        var parser = new HtmlParser();
        var doc = parser.ParseDocument($"<body>{content}</body>");

        foreach (var img in doc.QuerySelectorAll("img[src]"))
        {
            var src = img.GetAttribute("src");
            if (!string.IsNullOrEmpty(src)) urls.Add(src);
        }

        foreach (var source in doc.QuerySelectorAll("video source[src], video[src]"))
        {
            var src = source.GetAttribute("src");
            if (!string.IsNullOrEmpty(src)) urls.Add(src);
        }

        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrEmpty(href)) continue;
            var ext = Path.GetExtension(href).ToLower().Split('?')[0];
            if (ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".zip" or ".rar")
                urls.Add(href);
        }

        return urls.Distinct().ToList();
    }
}
