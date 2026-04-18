using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Holomove;

public partial class WpMigrator
{
    private readonly SiteConfig _config;
    private readonly HttpClient _httpClient;
    private string? _jwtToken;

    // Data collections
    private readonly List<WpPost> _sourcePosts = [];
    private List<WpTag> _sourceTags = [];
    private List<WpUser> _sourceAuthors = [];
    private List<WpCategory> _sourceCategories = [];
    private readonly List<WpPost> _targetPosts = [];
    private List<WpTag> _targetTags = [];
    private List<WpUser> _targetAuthors = [];
    private List<WpCategory> _targetCategories = [];

    // Lookup dictionaries
    private Dictionary<int, WpTag> _sourceTagsDict = new();
    private Dictionary<int, WpUser> _sourceUsersDict = new();
    private Dictionary<int, WpCategory> _sourceCategoriesDict = new();
    private Dictionary<string, WpTag> _targetTagsDict = new();
    private Dictionary<string, WpCategory> _targetCategoriesDict = new();

    // Target media lookup (concurrent — written during parallel uploads)
    private readonly ConcurrentDictionary<string, MediaItem> _targetMediaByUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MediaItem> _targetMediaByFilename = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, bool> _targetMediaIds = new();

    // Source media URL lookup by ID (built once from featured_media IDs in source posts)
    private readonly ConcurrentDictionary<int, string> _sourceMediaById = new();

    // Upload failure log (first N shown at end of run)
    private readonly ConcurrentBag<string> _uploadErrors = new();

    // Serialize re-auth on 401
    private readonly SemaphoreSlim _authLock = new(1, 1);

    // Backup media data (slug -> URLs) — written in parallel during SavePostToBackup
    private readonly ConcurrentDictionary<string, string> _backupFeaturedMediaUrls = new();
    private readonly ConcurrentDictionary<string, List<string>> _backupMediaUrls = new();

    // Backup file index (filename -> full path) for O(1) lookups
    private Dictionary<string, string> _backupFileIndex = new(StringComparer.OrdinalIgnoreCase);

    // Target author lookup by slug/name
    private Dictionary<string, WpUser> _targetAuthorsBySlug = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, WpUser> _targetAuthorsByName = new(StringComparer.OrdinalIgnoreCase);

    // Posts verified as fully synced — skip on subsequent runs
    private readonly ConcurrentDictionary<string, bool> _verifiedPosts = new(StringComparer.OrdinalIgnoreCase);

    public WpMigrator(SiteConfig config)
    {
        _config = config;
        var retryHandler = new RetryHandler(new HttpClientHandler())
        {
            MaxRetries = 3,
            ReauthAsync = ReauthAsync
        };
        _httpClient = new HttpClient(retryHandler) { Timeout = TimeSpan.FromMinutes(30) };
    }

    private async Task<string?> ReauthAsync()
    {
        await _authLock.WaitAsync();
        try
        {
            await Authenticate();
            return _jwtToken;
        }
        finally { _authLock.Release(); }
    }

    public async Task Init()
    {
        await Authenticate();
        InitBackup();
        LoadSourcePostsFromBackup();
        LoadTargetPostCache();
        LoadVerifiedPostsCache();
    }

    public async Task Migrate()
    {
        // 1. Fetch source data
        await FetchSourceData();
        BuildLookupDictionaries();

        // 2. Save to local backup
        Console.WriteLine("\n  Saving to local backup...");
        var postsToBackup = _sourcePosts.Where(p => !PostExistsInBackup(p)).ToList();
        if (postsToBackup.Count > 0)
        {
            var backupProgress = new ProgressBar();
            var backed = 0;
            await Parallel.ForEachAsync(postsToBackup, new ParallelOptions { MaxDegreeOfParallelism = 5 },
                async (post, _) =>
                {
                    await SavePostToBackup(post);
                    var count = Interlocked.Increment(ref backed);
                    backupProgress.Update(count, postsToBackup.Count, post.Slug);
                });
            backupProgress.Complete($"Backed up {postsToBackup.Count} new post(s).");
        }
        else
        {
            Console.WriteLine("  All posts already backed up.");
        }
        await SyncAllBackupMedia();
        await SaveAuthorsToBackup();
        await SaveTaxonomyToBackup();
        await SaveMetadataToBackup();

        BuildBackupFileIndex();

        // 3. Fetch target data
        await FetchTargetData();
        await DownloadTargetMediaList();
        BuildLookupDictionaries();

        // 4. Sync to target
        await SyncAuthors();
        BuildLookupDictionaries();
        await SyncTaxonomy();
        BuildLookupDictionaries();
        await SyncAllPosts();

        // 5. Cleanup
        await FindAndDeleteDuplicates();
        SaveTargetMediaCache();
        SaveVerifiedPostsCache();

        ReportUploadErrors();
        Console.WriteLine("\n  Migration complete!");
    }

    public async Task Repair()
    {
        _verifiedPosts.Clear();

        // Only need backup index + target data — skip source fetch & backup phases
        BuildBackupFileIndex();

        await FetchTargetData();
        await DownloadTargetMediaList();
        BuildLookupDictionaries();

        await RepairAllPosts();

        SaveTargetMediaCache();
        SaveVerifiedPostsCache();

        ReportUploadErrors();
        Console.WriteLine("\n  Repair complete!");
    }

    private void ReportUploadErrors()
    {
        if (_uploadErrors.IsEmpty) return;

        var distinct = _uploadErrors.Distinct().ToList();
        Console.WriteLine($"\n  {distinct.Count} upload error(s):");
        foreach (var err in distinct.Take(20))
            Console.WriteLine($"    - {err}");
        if (distinct.Count > 20)
            Console.WriteLine($"    … and {distinct.Count - 20} more");
    }

    public async Task Status()
    {
        await FetchSourceData();
        BuildLookupDictionaries();
        await FetchTargetData();
        BuildLookupDictionaries();
        await PrintStatus();
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(_jwtToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
        return request;
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string url, object payload)
    {
        var request = CreateAuthenticatedRequest(HttpMethod.Post, url);
        request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        return await _httpClient.SendAsync(request);
    }

    /// <summary>
    /// Fetch one page from a WP REST endpoint. Returns items and the X-WP-TotalPages value.
    /// </summary>
    private async Task<(List<T> Items, int TotalPages)> FetchPageAsync<T>(
        string apiUrl, string endpoint, int page, bool useAuth = false, string? extraQuery = null)
    {
        try
        {
            var url = $"{apiUrl}wp/v2/{endpoint}?per_page=100&page={page}";
            if (!string.IsNullOrEmpty(extraQuery)) url += $"&{extraQuery}";

            var response = useAuth
                ? await _httpClient.SendAsync(CreateAuthenticatedRequest(HttpMethod.Get, url))
                : await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode) return ([], 0);

            var totalPages = 1;
            if (response.Headers.TryGetValues("X-WP-TotalPages", out var values) &&
                int.TryParse(values.FirstOrDefault(), out var tp))
                totalPages = tp;

            var json = await response.Content.ReadAsStringAsync();
            var items = JsonConvert.DeserializeObject<List<T>>(json) ?? [];
            return (items, totalPages);
        }
        catch
        {
            return ([], 0);
        }
    }

    /// <summary>
    /// Fetch all pages from a WP REST endpoint in parallel. Uses X-WP-TotalPages from page 1.
    /// </summary>
    private async Task<List<T>> FetchAllPaginated<T>(string apiUrl, string endpoint, bool useAuth = false, string? extraQuery = null)
    {
        var (firstItems, totalPages) = await FetchPageAsync<T>(apiUrl, endpoint, 1, useAuth, extraQuery);
        if (totalPages <= 1) return firstItems;

        var pageResults = new List<T>?[totalPages + 1];
        pageResults[1] = firstItems;

        await Parallel.ForEachAsync(Enumerable.Range(2, totalPages - 1),
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            async (page, _) =>
            {
                var (items, _) = await FetchPageAsync<T>(apiUrl, endpoint, page, useAuth, extraQuery);
                pageResults[page] = items;
            });

        var all = new List<T>(firstItems.Count * totalPages);
        for (var i = 1; i <= totalPages; i++)
            if (pageResults[i] != null) all.AddRange(pageResults[i]!);
        return all;
    }

    private async Task<T?> CreateOnTarget<T>(string endpoint, object payload) where T : class
    {
        try
        {
            var response = await PostJsonAsync($"{_config.TargetWpApiUrl}wp/v2/{endpoint}", payload);
            var json = await response.Content.ReadAsStringAsync();
            return response.IsSuccessStatusCode ? JsonConvert.DeserializeObject<T>(json) : null;
        }
        catch { return null; }
    }

    private void BuildLookupDictionaries()
    {
        _sourceTagsDict = _sourceTags.ToDictionary(t => t.Id);
        _sourceUsersDict = _sourceAuthors.ToDictionary(u => u.Id);
        _sourceCategoriesDict = _sourceCategories.ToDictionary(c => c.Id);
        _targetTagsDict = _targetTags.ToDictionary(t => t.Slug);
        _targetCategoriesDict = _targetCategories.ToDictionary(c => c.Slug);
        _targetAuthorsBySlug = _targetAuthors.ToDictionary(a => a.Slug, StringComparer.OrdinalIgnoreCase);
        _targetAuthorsByName = _targetAuthors
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    private void BuildBackupFileIndex()
    {
        var postsDir = Path.Combine(_backupRoot, "posts");
        if (!Directory.Exists(postsDir)) return;

        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(postsDir, "*.*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name == "post.json") continue;
            index.TryAdd(name, file);
        }

        _backupFileIndex = index;
        Console.WriteLine($"  Indexed {index.Count} backup media files.");
    }

    private string RewriteContentUrls(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        // Match all source-domain media URLs (covers src, srcset, href, etc.)
        var pattern = Regex.Escape(_config.SourceWpUrl) + @"/wp-content/uploads/[^\s""'<>\)]+";

        return Regex.Replace(content, pattern, match =>
        {
            var sourceUrl = match.Value;
            try
            {
                var fileName = Path.GetFileName(new Uri(sourceUrl).LocalPath);

                // Exact filename match → use actual target URL
                if (_targetMediaByFilename.TryGetValue(fileName, out var media) &&
                    !string.IsNullOrEmpty(media.SourceUrl))
                    return media.SourceUrl;

                // Size variant (photo-300x200.jpg) → derive from base file's target path
                var sizeMatch = Regex.Match(fileName, @"^(.+)-\d+x\d+(\.[^.]+)$");
                if (sizeMatch.Success)
                {
                    var baseFileName = sizeMatch.Groups[1].Value + sizeMatch.Groups[2].Value;
                    if (_targetMediaByFilename.TryGetValue(baseFileName, out var baseMedia) &&
                        !string.IsNullOrEmpty(baseMedia.SourceUrl))
                    {
                        var lastSlash = baseMedia.SourceUrl.LastIndexOf('/');
                        if (lastSlash >= 0)
                            return baseMedia.SourceUrl[..lastSlash] + "/" + fileName;
                    }
                }
            }
            catch { /* leave URL unchanged */ }

            return sourceUrl;
        });
    }

    private void TrackTargetMedia(MediaItem media)
    {
        if (media.Id > 0) _targetMediaIds[media.Id] = true;
        if (!string.IsNullOrEmpty(media.SourceUrl))
        {
            _targetMediaByUrl[media.SourceUrl] = media;
            var filename = Path.GetFileName(new Uri(media.SourceUrl).LocalPath);
            if (!string.IsNullOrEmpty(filename))
                _targetMediaByFilename[filename] = media;
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
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            _ => "application/octet-stream"
        };
    }
}
