using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Holomove;

public partial class WpMigrator
{
    private async Task DownloadTargetMediaList()
    {
        Console.WriteLine("\nDownloading target media list...");
        var page = 1;
        var totalMedia = 0;

        while (true)
        {
            try
            {
                var url = $"{Config.TargetWpApiUrl}wp/v2/media?per_page=100&page={page}&_fields=id,source_url";
                var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        break;
                    Console.WriteLine($"\n  Error fetching page {page}: {response.StatusCode}");
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
                        {
                            _targetMediaByFilename[filename] = media;
                        }
                    }
                    totalMedia++;
                }

                var width = Math.Max(Console.WindowWidth, 80);
                Console.Write($"\r  Indexing media: {totalMedia} items (page {page})".PadRight(width - 1));
                page++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  Error on page {page}: {ex.Message}");
                break;
            }
        }

        Console.WriteLine($"\n  Total media indexed: {_targetMediaByUrl.Count} by URL, {_targetMediaByFilename.Count} by filename");
    }

    private async Task<string> ProcessContentMedia(string content, List<int>? uploadedMediaIds = null)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument($"<body>{content}</body>");

        // Process images
        foreach (var img in doc.QuerySelectorAll("img[src]"))
        {
            var src = img.GetAttribute("src") ?? "";
            if (string.IsNullOrEmpty(src) || !IsSourceMedia(src)) continue;

            var uploaded = await UploadMedia(src);
            if (uploaded == null) continue;

            img.SetAttribute("src", uploaded.SourceUrl);
            img.RemoveAttribute("srcset");
            uploadedMediaIds?.Add(uploaded.Id);
        }

        // Process video sources
        foreach (var source in doc.QuerySelectorAll("video source[src], video[src]"))
        {
            var src = source.GetAttribute("src") ?? "";
            if (string.IsNullOrEmpty(src) || !IsSourceMedia(src)) continue;

            var uploaded = await UploadMedia(src);
            if (uploaded != null)
            {
                source.SetAttribute("src", uploaded.SourceUrl);
                uploadedMediaIds?.Add(uploaded.Id);
            }
        }

        // Process anchor links to media files
        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href") ?? "";
            if (string.IsNullOrEmpty(href) || !IsSourceMedia(href)) continue;

            var ext = Path.GetExtension(href).ToLower().Split('?')[0];
            if (ext is not (".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".zip" or ".rar")) continue;
            var uploaded = await UploadMedia(href);
            if (uploaded == null) continue;
            anchor.SetAttribute("href", uploaded.SourceUrl);
            uploadedMediaIds?.Add(uploaded.Id);
        }

        return doc.Body?.InnerHtml ?? content;
    }

    private static bool IsSourceMedia(string url)
    {
        return url.Contains(Config.SourceWpUrl) || url.Contains("holographica.space/wp-content/uploads");
    }

    private async Task<string?> GetMediaUrl(int mediaId)
    {
        try
        {
            var url = $"{Config.SourceWpApiUrl}wp/v2/media/{mediaId}?_fields=source_url";
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

            // Check if media already exists on target
            var targetUrl = sourceUrl.Replace(Config.SourceWpUrl, Config.TargetWpUrl);

            MediaItem? existingMedia = null;
            if (_targetMediaByUrl.TryGetValue(targetUrl, out var byUrl))
            {
                existingMedia = byUrl;
            }
            else if (_targetMediaByFilename.TryGetValue(originalFileName, out var byFilename))
            {
                existingMedia = byFilename;
            }
            else if (_targetMediaByFilename.TryGetValue(fileName, out var bySanitizedFilename))
            {
                existingMedia = bySanitizedFilename;
            }

            if (existingMedia != null)
            {
                Console.WriteLine($"  [Exists] {fileName}");
                if (attachToPostId.HasValue && existingMedia.Id > 0)
                {
                    await AttachMediaToPost(existingMedia.Id, attachToPostId.Value);
                }
                return existingMedia;
            }

            Console.Write($"  Downloading: {fileName}...");

            using var downloadResponse = await _httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!downloadResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($" Failed ({downloadResponse.StatusCode})");
                return null;
            }

            var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
            await using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();

            var buffer = new byte[81920];
            int bytesRead;
            long totalRead = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while ((bytesRead = await downloadStream.ReadAsync(buffer)) > 0)
            {
                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                if (totalBytes <= 0 || sw.ElapsedMilliseconds <= 500) continue;
                var pct = totalRead * 100 / totalBytes;
                Console.Write($"\r  Downloading: {fileName}... {pct}%   ");
            }

            var mediaBytes = memoryStream.ToArray();
            Console.Write($"\r  Uploading: {fileName} ({mediaBytes.Length / 1024}KB)...   ");

            var uploadUrl = $"{Config.TargetWpApiUrl}wp/v2/media";
            if (attachToPostId.HasValue)
            {
                uploadUrl += $"?post={attachToPostId.Value}";
            }
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

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($" Upload failed");
                return null;
            }

            var result = JsonConvert.DeserializeObject<MediaItem>(json);
            Console.WriteLine($"\r  Uploaded: {fileName} -> ID {result?.Id}                    ");

            if (result == null || string.IsNullOrEmpty(result.SourceUrl)) return result;
            _targetMediaByUrl[result.SourceUrl] = result;
            _targetMediaByFilename[fileName] = result;

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Error: {ex.Message}");
            return null;
        }
    }

    private async Task AttachMediaToPost(int mediaId, int postId)
    {
        try
        {
            var response = await PostJsonAsync($"{Config.TargetWpApiUrl}wp/v2/media/{mediaId}", new { post = postId });
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"  Warning: Failed to attach media {mediaId} to post {postId}: {error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Error attaching media: {ex.Message}");
        }
    }

    private async Task SetPostFeaturedImage(int postId, int mediaId)
    {
        try
        {
            var response = await PostJsonAsync($"{Config.TargetWpApiUrl}wp/v2/posts/{postId}", new { featured_media = mediaId });
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"  Warning: Failed to set featured image: {error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Error setting featured image: {ex.Message}");
        }
    }

    private async Task SetPostFeaturedVideo(int postId, string videoUrl)
    {
        try
        {
            var serialized = $"a:1:{{s:8:\"td_video\";s:{videoUrl.Length}:\"{videoUrl}\";}}";

            var response = await PostJsonAsync($"{Config.TargetWpApiUrl}wp/v2/posts/{postId}", new
            {
                meta = new Dictionary<string, object>
                {
                    ["td_post_video"] = new[] { serialized },
                    ["td_post_video_duration"] = new[] { "" }
                }
            });
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"  Warning: Failed to set featured video: {error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Error setting featured video: {ex.Message}");
        }
    }

    private static string? ExtractVideoUrl(string iframeSrc)
    {
        var ytMatch = YoutubeIframeRegex().Match(iframeSrc);
        if (ytMatch.Success) return $"https://www.youtube.com/watch?v={ytMatch.Groups[1].Value}";

        var vimeoMatch = VimeoIframeRegex().Match(iframeSrc);
        if (vimeoMatch.Success) return $"https://vimeo.com/{vimeoMatch.Groups[1].Value}";

        var dmMatch = DailymotionIframeRegex().Match(iframeSrc);
        return dmMatch.Success ? $"https://www.dailymotion.com/video/{dmMatch.Groups[1].Value}" : null;
    }

    [GeneratedRegex(@"[^\u0000-\u007F]+")]
    private static partial Regex NonAsciiRegex();
    [GeneratedRegex(@"youtube\.com/embed/([a-zA-Z0-9_-]+)")]
    private static partial Regex YoutubeIframeRegex();
    [GeneratedRegex(@"player\.vimeo\.com/video/(\d+)")]
    private static partial Regex VimeoIframeRegex();
    [GeneratedRegex(@"dailymotion\.com/embed/video/([a-zA-Z0-9]+)")]
    private static partial Regex DailymotionIframeRegex();

    private static List<string> ExtractMediaUrls(string content)
    {
        var urls = new List<string>();
        var parser = new HtmlParser();
        var doc = parser.ParseDocument($"<body>{content}</body>");

        foreach (var img in doc.QuerySelectorAll("img[src]"))
        {
            var src = img.GetAttribute("src");
            if (!string.IsNullOrEmpty(src))
                urls.Add(src);
        }

        foreach (var source in doc.QuerySelectorAll("video source[src], video[src]"))
        {
            var src = source.GetAttribute("src");
            if (!string.IsNullOrEmpty(src))
                urls.Add(src);
        }

        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrEmpty(href)) continue;

            var ext = Path.GetExtension(href).ToLower().Split('?')[0];
            if (ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".zip" or ".rar")
            {
                urls.Add(href);
            }
        }

        return urls.Distinct().ToList();
    }
}
