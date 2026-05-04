using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Holomove;

public partial class WpMigrator
{
    private async Task DownloadTargetMediaList()
    {
        LoadTargetMediaCache();

        Console.Write("  Checking for new media on target...");
        var fetched = 0;

        // Page 1 reveals totalPages. Remaining pages fetched in parallel.
        var (firstPage, totalPages) = await FetchPageAsync<MediaItem>(
            _config.TargetWpApiUrl, "media", 1, useAuth: true, extraQuery: "_fields=id,source_url");

        foreach (var media in firstPage)
        {
            if (_targetMediaIds.ContainsKey(media.Id)) continue;
            TrackTargetMedia(media);
            fetched++;
        }

        if (totalPages > 1)
        {
            var pagesDone = 1;
            await Parallel.ForEachAsync(Enumerable.Range(2, totalPages - 1),
                new ParallelOptions { MaxDegreeOfParallelism = 6 },
                async (page, _) =>
                {
                    var (items, _) = await FetchPageAsync<MediaItem>(
                        _config.TargetWpApiUrl, "media", page, useAuth: true, extraQuery: "_fields=id,source_url");
                    foreach (var media in items)
                    {
                        if (_targetMediaIds.ContainsKey(media.Id)) continue;
                        TrackTargetMedia(media);
                        Interlocked.Increment(ref fetched);
                    }
                    var done = Interlocked.Increment(ref pagesDone);
                    Console.Write($"\r  Target media: page {done}/{totalPages} ({_targetMediaByUrl.Count} items)".PadRight(78));
                });
        }

        Console.WriteLine($"\r  Target media: {_targetMediaByUrl.Count} items ({fetched} new)                    ");
        if (fetched > 0) SaveTargetMediaCache();
    }

    /// <summary>
    /// Build an in-memory map from source featured_media IDs to their source_url,
    /// fetched in batches of 100 via ?include=… — replaces N per-post GetMediaUrl calls.
    /// </summary>
    private async Task BuildSourceMediaIndex()
    {
        var ids = _sourcePosts
            .Select(p => p.FeaturedMedia)
            .Where(id => id > 0 && !_sourceMediaById.ContainsKey(id))
            .Distinct()
            .ToList();

        if (ids.Count == 0) return;

        Console.Write($"  Resolving {ids.Count} featured media URLs...");

        var batches = ids.Chunk(100).ToList();
        var resolved = 0;

        await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = 6 },
            async (batch, _) =>
            {
                var csv = string.Join(",", batch);
                var url = $"{_config.SourceWpApiUrl}wp/v2/media?include={csv}&per_page=100&_fields=id,source_url";
                try
                {
                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode) return;

                    var json = await response.Content.ReadAsStringAsync();
                    var items = JsonConvert.DeserializeObject<List<MediaItem>>(json) ?? [];
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrEmpty(item.SourceUrl))
                        {
                            _sourceMediaById[item.Id] = item.SourceUrl;
                            Interlocked.Increment(ref resolved);
                        }
                    }
                }
                catch { /* ignore — GetMediaUrl fallback will handle individually */ }
            });

        Console.WriteLine($"\r  Resolved {resolved} featured media URLs ({batches.Count} batches).".PadRight(60));
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
                TrackTargetMedia(media);
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
    private async Task<List<int>> UploadAllPostMedia(string content, int? attachToPostId = null, string? postLink = null)
    {
        var mediaUrls = ExtractMediaUrls(content).Where(IsSourceMedia).ToList();
        if (mediaUrls.Count == 0) return [];

        var uploadedIds = new System.Collections.Concurrent.ConcurrentBag<int>();

        await Parallel.ForEachAsync(mediaUrls, new ParallelOptions { MaxDegreeOfParallelism = 3 },
            async (url, _) =>
            {
                var uploaded = await UploadMedia(url, attachToPostId, postLink);
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
    private async Task<MediaItem?> UploadMedia(string sourceUrl, int? attachToPostId = null, string? postLink = null)
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

            // Size variants (photo-300x200.jpg) aren't separate media library records —
            // WP regenerates them from the base file on demand. Match against the base.
            var (baseOriginal, baseSanitized) = StripSizeSuffix(originalFileName, fileName);

            MediaItem? existingMedia = null;
            if (_targetMediaByUrl.TryGetValue(targetUrl, out var byUrl))
                existingMedia = byUrl;
            else if (_targetMediaByFilename.TryGetValue(originalFileName, out var byFilename))
                existingMedia = byFilename;
            else if (_targetMediaByFilename.TryGetValue(fileName, out var bySanitizedFilename))
                existingMedia = bySanitizedFilename;
            else if (baseOriginal != originalFileName &&
                     _targetMediaByFilename.TryGetValue(baseOriginal, out var byBase))
                existingMedia = byBase;
            else if (baseSanitized != fileName &&
                     _targetMediaByFilename.TryGetValue(baseSanitized, out var byBaseSanitized))
                existingMedia = byBaseSanitized;

            if (existingMedia != null)
            {
                if (attachToPostId.HasValue && existingMedia.Id > 0)
                    await AttachMediaToPost(existingMedia.Id, attachToPostId.Value);
                return existingMedia;
            }

            // For size variants, upload the BASE file instead of the variant —
            // WP regenerates variants on demand from the base. Uploading the
            // variant directly would create a separate media record that can't
            // generate further size variants properly.
            string lookupOriginal = originalFileName;
            string lookupSanitized = fileName;
            if (baseOriginal != originalFileName)
            {
                lookupOriginal = baseOriginal;
                lookupSanitized = baseSanitized;
            }

            var backupFile = FindInBackup(lookupOriginal) ?? FindInBackup(lookupSanitized);
            if (backupFile == null)
            {
                _uploadErrors.Add($"{lookupOriginal}: not in backup{FormatPostLink(postLink)}");
                return null;
            }
            // Use the base name for the upload so WP stores it canonically.
            originalFileName = lookupOriginal;
            fileName = lookupSanitized;

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

            var response = await SendWriteAsync(() => _httpClient.SendAsync(request));
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _uploadErrors.Add($"{fileName}: HTTP {(int)response.StatusCode} — {Truncate(json, 200)}{FormatPostLink(postLink)}");
                return null;
            }

            var result = JsonConvert.DeserializeObject<MediaItem>(json);
            if (result != null) TrackTargetMedia(result);
            return result;
        }
        catch (Exception ex)
        {
            _uploadErrors.Add($"{sourceUrl}: {ex.Message}{FormatPostLink(postLink)}");
            return null;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    private static string FormatPostLink(string? postLink) =>
        string.IsNullOrEmpty(postLink) ? "" : $" (post: {postLink})";

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
        if (string.IsNullOrEmpty(fileName)) return null;
        return _backupFileIndex.TryGetValue(fileName, out var path) ? path : null;
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
                var (baseOriginal, baseSanitized) = StripSizeSuffix(fileName, sanitized);

                return !_targetMediaByUrl.ContainsKey(targetUrl) &&
                       !_targetMediaByFilename.ContainsKey(fileName) &&
                       !_targetMediaByFilename.ContainsKey(sanitized) &&
                       !_targetMediaByFilename.ContainsKey(baseOriginal) &&
                       !_targetMediaByFilename.ContainsKey(baseSanitized);
            })
            .ToList();
    }

    private static readonly Regex SizeSuffixRegex = new(@"^(.+)-\d+x\d+(\.[^.]+)$", RegexOptions.Compiled);

    private static (string OriginalBase, string SanitizedBase) StripSizeSuffix(string original, string sanitized)
    {
        var origBase = original;
        var origMatch = SizeSuffixRegex.Match(original);
        if (origMatch.Success) origBase = origMatch.Groups[1].Value + origMatch.Groups[2].Value;

        var sanBase = sanitized;
        var sanMatch = SizeSuffixRegex.Match(sanitized);
        if (sanMatch.Success) sanBase = sanMatch.Groups[1].Value + sanMatch.Groups[2].Value;

        return (origBase, sanBase);
    }

    [GeneratedRegex(@"[^\u0000-\u007F]+")]
    private static partial Regex NonAsciiRegex();

    [GeneratedRegex("""<(?:img|video|source)\b[^>]*?\ssrc\s*=\s*["']([^"'<>]+)["']""",
        RegexOptions.IgnoreCase, "en-AE")]
    private static partial Regex MediaSrcRegex();

    [GeneratedRegex("""<a\b[^>]*?\shref\s*=\s*["']([^"'<>]+\.(?:pdf|docx?|xlsx?|pptx?|zip|rar)(?:\?[^"'<>]*)?)["']""",
        RegexOptions.IgnoreCase, "en-AE")]
    private static partial Regex DocumentLinkRegex();

    private static List<string> ExtractMediaUrls(string content)
    {
        if (string.IsNullOrEmpty(content)) return [];

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in MediaSrcRegex().Matches(content))
            urls.Add(m.Groups[1].Value);

        foreach (Match m in DocumentLinkRegex().Matches(content))
            urls.Add(m.Groups[1].Value);

        return urls.ToList();
    }
}
