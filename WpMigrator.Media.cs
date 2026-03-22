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
        Console.WriteLine("\n  Downloading target media list...");
        var page = 1;
        var totalMedia = 0;

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

                foreach (var media in mediaItems)
                {
                    if (!string.IsNullOrEmpty(media.SourceUrl))
                    {
                        _targetMediaByUrl[media.SourceUrl] = media;

                        var filename = Path.GetFileName(new Uri(media.SourceUrl).LocalPath);
                        if (!string.IsNullOrEmpty(filename))
                            _targetMediaByFilename[filename] = media;
                    }
                    totalMedia++;
                }

                var width = Math.Max(Console.WindowWidth, 80);
                Console.Write($"\r  Indexing media: {totalMedia} items (page {page})".PadRight(width - 1));
                page++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  Error on media page {page}: {ex.Message}");
                break;
            }
        }

        Console.WriteLine($"\n  Total media indexed: {_targetMediaByUrl.Count} by URL, {_targetMediaByFilename.Count} by filename");
    }

    /// <summary>
    /// Uploads all media from source content to target (so files exist there).
    /// Does NOT modify content — URLs stay as source domain.
    /// Returns list of uploaded media IDs for post attachment.
    /// </summary>
    private async Task<List<int>> UploadAllPostMedia(string content)
    {
        var mediaUrls = ExtractMediaUrls(content).Where(IsSourceMedia).ToList();
        if (mediaUrls.Count == 0) return [];

        var uploadedIds = new System.Collections.Concurrent.ConcurrentBag<int>();

        await Parallel.ForEachAsync(mediaUrls, new ParallelOptions { MaxDegreeOfParallelism = 3 },
            async (url, _) =>
            {
                var uploaded = await UploadMedia(url);
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

            using var downloadResponse = await _httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!downloadResponse.IsSuccessStatusCode) return null;

            await using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();
            await downloadStream.CopyToAsync(memoryStream);

            var mediaBytes = memoryStream.ToArray();

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
