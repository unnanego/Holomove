using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WordPressPCL;
using WordPressPCL.Models;
// ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

// =============================================================================
// CONFIGURATION
// =============================================================================

var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "downloads");
Directory.CreateDirectory(dataFolder);

// Source WordPress (read-only, no auth needed)
const string sourceWpUrl = "https://holographica.space";
const string sourceWpApiUrl = $"{sourceWpUrl}/wp-json/";

// Target WordPress (needs JWT authentication)
const string targetWpUrl = "https://new.holographica.space";
const string targetWpApiUrl = $"{targetWpUrl}/wp-json/";
const string targetWpUsername = "unnanego";
const string targetWpPassword = "cezmx6#DBjJrjL#rOy";

// =============================================================================
// INITIALIZATION
// =============================================================================

var sourceWp = new WordPressClient(sourceWpApiUrl);
var targetWp = new WordPressClient(targetWpApiUrl);

// HTTP client with retry handler
var retryHandler = new RetryHandler(new HttpClientHandler()) { MaxRetries = 3 };
var httpClient = new HttpClient(retryHandler) { Timeout = TimeSpan.FromMinutes(30) };

// JWT token for target WordPress
string? jwtToken = null;

// Data collections
List<Post> sourcePosts = [];
List<Tag> sourceTags = [];
List<User> sourceAuthors = [];
List<Category> sourceCategories = [];
List<Post> targetPosts = [];
List<Tag> targetTags = [];
List<User> targetAuthors = [];
List<Category> targetCategories = [];

// Lookup dictionaries
var sourceTagsDict = new Dictionary<int, Tag>();
var sourceUsersDict = new Dictionary<int, User>();
var sourceCategoriesDict = new Dictionary<int, Category>();
var targetTagsDict = new Dictionary<string, Tag>();
var targetAuthorsDict = new Dictionary<string, User>();
var targetCategoriesDict = new Dictionary<string, Category>();

// Target media lookup (URL -> MediaItem)
var targetMediaByUrl = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);
var targetMediaByFilename = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);


// =============================================================================
// MAIN EXECUTION
// =============================================================================

await AuthenticateTargetWp();

await LoadCache();
await DownloadTargetMediaList();
await VerifyAndFixExistingPostsMedia();
await SaveCache();
return;

await DownloadAllPostsAndCompare();
var duplicates = FindDuplicates();
await DeleteDuplicatePosts(duplicates);
await MigrateAuthors();
await UpdateExistingPostsAuthors();
await ProcessPosts();
await VerifyAndFixExistingPostsMedia();
await SaveCache();

Console.WriteLine("\nMigration complete!");

// =============================================================================
// AUTHENTICATION
// =============================================================================

async Task AuthenticateTargetWp()
{
    Console.WriteLine("Authenticating with target WordPress...");

    // MiniOrange JWT plugin endpoint
    var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"{targetWpApiUrl}api/v1/token");
    tokenRequest.Content = new StringContent(
        JsonConvert.SerializeObject(new { username = targetWpUsername, password = targetWpPassword }),
        Encoding.UTF8,
        "application/json");

    var response = await httpClient.SendAsync(tokenRequest);
    var json = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"JWT authentication failed: {json}");
        Console.WriteLine("Make sure JWT Authentication plugin is installed and configured.");
        Environment.Exit(1);
    }

    Console.WriteLine($"Response: {json}");

    var result = JObject.Parse(json);
    // Try different token field names used by various JWT plugins
    jwtToken = result["token"]?.ToString()
            ?? result["jwt_token"]?.ToString()
            ?? result["access_token"]?.ToString()
            ?? result["data"]?["token"]?.ToString();

    if (string.IsNullOrEmpty(jwtToken))
    {
        Console.WriteLine("Failed to get JWT token from response");
        Environment.Exit(1);
    }

    // Configure WordPressPCL to use JWT
    targetWp.Auth.SetJWToken(jwtToken);

    var displayName = result["user_display_name"]?.ToString()
                   ?? result["name"]?.ToString()
                   ?? result["user"]?.ToString()
                   ?? "unknown";
    Console.WriteLine($"Authenticated as: {displayName}");
}

HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url)
{
    var request = new HttpRequestMessage(method, url);
    if (!string.IsNullOrEmpty(jwtToken))
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
    }
    return request;
}

// =============================================================================
// TARGET MEDIA LIST
// =============================================================================

async Task DownloadTargetMediaList()
{
    Console.WriteLine("\nDownloading target media list...");
    var page = 1;
    var totalMedia = 0;

    while (true)
    {
        try
        {
            var url = $"{targetWpApiUrl}wp/v2/media?per_page=100&page={page}&_fields=id,source_url";
            var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
            var response = await httpClient.SendAsync(request);

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
                    targetMediaByUrl[media.SourceUrl] = media;

                    // Also index by filename for flexible matching
                    var filename = Path.GetFileName(new Uri(media.SourceUrl).LocalPath);
                    if (!string.IsNullOrEmpty(filename))
                    {
                        targetMediaByFilename[filename] = media;
                    }
                }
                totalMedia++;
            }

            Console.Write($"\r  Downloaded: {totalMedia} media items (page {page})          ");
            page++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  Error on page {page}: {ex.Message}");
            break;
        }
    }

    Console.WriteLine($"\n  Total media indexed: {targetMediaByUrl.Count} by URL, {targetMediaByFilename.Count} by filename");
}

// =============================================================================
// CACHE MANAGEMENT
// =============================================================================

async Task LoadCache()
{
    Console.WriteLine("\nLoading cached data...");

    sourcePosts = await LoadFromCache<List<Post>>("sourcePosts.json") ?? [];
    sourceTags = await LoadFromCache<List<Tag>>("sourceTags.json") ?? [];
    sourceAuthors = await LoadFromCache<List<User>>("sourceAuthors.json") ?? [];
    sourceCategories = await LoadFromCache<List<Category>>("sourceCategories.json") ?? [];
    targetPosts = await LoadFromCache<List<Post>>("targetPosts.json") ?? [];
    targetTags = await LoadFromCache<List<Tag>>("targetTags.json") ?? [];
    targetAuthors = await LoadFromCache<List<User>>("targetAuthors.json") ?? [];
    targetCategories = await LoadFromCache<List<Category>>("targetCategories.json") ?? [];

    Console.WriteLine($"  Source: {sourcePosts.Count} posts, {sourceTags.Count} tags, {sourceCategories.Count} categories");
    Console.WriteLine($"  Target: {targetPosts.Count} posts, {targetTags.Count} tags, {targetCategories.Count} categories");
}

async Task SaveCache()
{
    Console.WriteLine("\nSaving cache...");

    await SaveToCache("sourcePosts.json", sourcePosts);
    await SaveToCache("sourceTags.json", sourceTags);
    await SaveToCache("sourceAuthors.json", sourceAuthors);
    await SaveToCache("sourceCategories.json", sourceCategories);
    await SaveToCache("targetPosts.json", targetPosts);
    await SaveToCache("targetTags.json", targetTags);
    await SaveToCache("targetAuthors.json", targetAuthors);
    await SaveToCache("targetCategories.json", targetCategories);
}

async Task<T?> LoadFromCache<T>(string fileName) where T : class
{
    var path = Path.Combine(dataFolder, fileName);
    if (!File.Exists(path)) return null;

    try
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonConvert.DeserializeObject<T>(json);
    }
    catch
    {
        return null;
    }
}

async Task SaveToCache<T>(string fileName, T data)
{
    var path = Path.Combine(dataFolder, fileName);
    var json = JsonConvert.SerializeObject(data, Formatting.Indented);
    await File.WriteAllTextAsync(path, json);
}

// =============================================================================
// DATA DOWNLOAD AND COMPARISON
// =============================================================================

async Task DownloadAllPostsAndCompare()
{
    Console.WriteLine("\n" + new string('=', 60));
    Console.WriteLine("CACHE COMPARISON REPORT");
    Console.WriteLine(new string('=', 60));

    // Get existing cached IDs
    var cachedSourceIds = sourcePosts.Select(p => p.Id).ToHashSet();
    var cachedTargetIds = targetPosts.Select(p => p.Id).ToHashSet();
    var cachedSourceSlugs = sourcePosts.ToDictionary(p => p.Slug, p => p);
    var cachedTargetSlugs = targetPosts.ToDictionary(p => p.Slug, p => p);

    Console.WriteLine($"\nCached data:");
    Console.WriteLine($"  Source: {cachedSourceIds.Count} posts");
    Console.WriteLine($"  Target: {cachedTargetIds.Count} posts");

    // Fetch all post IDs from APIs
    Console.WriteLine("\nFetching post IDs from APIs...");
    var apiSourceIds = await GetAllPostIds(sourceWpApiUrl, useAuth: false);
    var apiTargetIds = await GetAllPostIds(targetWpApiUrl, useAuth: true);

    Console.WriteLine($"\nAPI data:");
    Console.WriteLine($"  Source: {apiSourceIds.Count} posts");
    Console.WriteLine($"  Target: {apiTargetIds.Count} posts");

    // ==========================================================================
    // COMPARISON: Cached vs API
    // ==========================================================================
    Console.WriteLine("\n" + new string('-', 60));
    Console.WriteLine("CACHE VS API COMPARISON");
    Console.WriteLine(new string('-', 60));

    // Source posts: stale (in cache but not on server)
    var staleSourceIds = cachedSourceIds.Except(apiSourceIds).ToList();
    // Source posts: missing from cache (on server but not in cache)
    var missingSourceIds = apiSourceIds.Except(cachedSourceIds).ToList();

    // Target posts: stale (in cache but not on server)
    var staleTargetIds = cachedTargetIds.Except(apiTargetIds).ToList();
    // Target posts: missing from cache (on server but not in cache)
    var missingTargetIds = apiTargetIds.Except(cachedTargetIds).ToList();

    Console.WriteLine($"\nSource posts:");
    Console.WriteLine($"  Stale (in cache but deleted from server): {staleSourceIds.Count}");
    Console.WriteLine($"  Missing from cache (need to download): {missingSourceIds.Count}");

    Console.WriteLine($"\nTarget posts:");
    Console.WriteLine($"  Stale (in cache but deleted from server): {staleTargetIds.Count}");
    Console.WriteLine($"  Missing from cache (need to download): {missingTargetIds.Count}");

    // Save stale/missing lists to files for reference
    var comparisonReport = new
    {
        GeneratedAt = DateTime.Now,
        Source = new
        {
            CachedCount = cachedSourceIds.Count,
            ApiCount = apiSourceIds.Count,
            StaleIds = staleSourceIds,
            MissingIds = missingSourceIds
        },
        Target = new
        {
            CachedCount = cachedTargetIds.Count,
            ApiCount = apiTargetIds.Count,
            StaleIds = staleTargetIds,
            MissingIds = missingTargetIds
        }
    };
    await SaveToCache("cacheComparisonReport.json", comparisonReport);
    Console.WriteLine($"\nComparison report saved to: cacheComparisonReport.json");

    // ==========================================================================
    // CLEANUP: Remove stale posts from cache
    // ==========================================================================
    if (staleSourceIds.Count > 0)
    {
        Console.WriteLine($"\nRemoving {staleSourceIds.Count} stale source post(s) from cache...");
        foreach (var id in staleSourceIds)
        {
            var post = sourcePosts.FirstOrDefault(p => p.Id == id);
            Console.WriteLine($"  - ID {id}: {post?.Slug ?? "unknown"}");
        }
        sourcePosts.RemoveAll(p => staleSourceIds.Contains(p.Id));
    }

    if (staleTargetIds.Count > 0)
    {
        Console.WriteLine($"\nRemoving {staleTargetIds.Count} stale target post(s) from cache...");
        foreach (var id in staleTargetIds)
        {
            var post = targetPosts.FirstOrDefault(p => p.Id == id);
            Console.WriteLine($"  - ID {id}: {post?.Slug ?? "unknown"}");
        }
        targetPosts.RemoveAll(p => staleTargetIds.Contains(p.Id));
    }

    // ==========================================================================
    // DOWNLOAD: Fetch missing posts
    // ==========================================================================
    if (missingSourceIds.Count > 0)
    {
        Console.WriteLine($"\nDownloading {missingSourceIds.Count} missing source posts...");
        var count = 0;
        foreach (var (id, index) in missingSourceIds.Select((id, i) => (id, i)))
        {
            Console.Write($"\r  Progress: {index + 1}/{missingSourceIds.Count} (ID: {id})          ");
            try
            {
                var post = await sourceWp.Posts.GetByIDAsync(id);
                sourcePosts.Add(post);
                count++;

                // Save cache every 100 posts
                if (count % 100 == 0)
                {
                    await SaveCache();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  Failed to download source post {id}: {ex.Message}");
            }
        }
        Console.WriteLine();
        await SaveCache();
    }

    Console.WriteLine($"\nTarget posts to download: {missingTargetIds.Count}");
    Console.WriteLine($"Target posts already in cache: {targetPosts.Count}");

    if (missingTargetIds.Count > 0)
    {
        Console.WriteLine($"Downloading missing target posts in batches...");
        var page = 1;

        while (true)
        {
            try
            {
                var url = $"{targetWpApiUrl}wp/v2/posts?per_page=100&page={page}";
                var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
                var response = await httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"\n  Error fetching page {page}: {response.StatusCode}");
                    Console.WriteLine($"  Response: {errorContent.Substring(0, Math.Min(500, errorContent.Length))}");
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        break; // No more pages
                    break;
                }

                var json = await response.Content.ReadAsStringAsync();
                var posts = JsonConvert.DeserializeObject<List<Post>>(json) ?? [];
                if (posts.Count == 0)
                {
                    Console.WriteLine($"  Page {page}: empty, stopping");
                    break;
                }

                foreach (var post in posts)
                {
                    if (!targetPosts.Any(p => p.Id == post.Id))
                    {
                        targetPosts.Add(post);
                    }
                }

                Console.WriteLine($"  Page {page}: {posts.Count} posts, total in cache: {targetPosts.Count}");
                page++;

                // Save periodically
                if (page % 10 == 0)
                {
                    await SaveCache();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  Error on page {page}: {ex.Message}");
                break;
            }
        }
        Console.WriteLine();
        await SaveCache();
    }

    Console.WriteLine($"\nCache state after downloads:");
    Console.WriteLine($"  Source posts: {sourcePosts.Count}");
    Console.WriteLine($"  Target posts: {targetPosts.Count}");

    // Verify we have data
    if (sourcePosts.Count == 0)
    {
        Console.WriteLine("\nERROR: No source posts loaded. Check source API connection.");
        Environment.Exit(1);
    }

    if (apiTargetIds.Count > 0 && targetPosts.Count == 0)
    {
        Console.WriteLine($"\nERROR: API reports {apiTargetIds.Count} target posts but cache is empty.");
        Console.WriteLine("Target post download failed. Check authentication and try again.");
        Environment.Exit(1);
    }

    // ==========================================================================
    // MIGRATION STATUS: Find posts missing from target
    // ==========================================================================
    Console.WriteLine("\n" + new string('-', 60));
    Console.WriteLine("MIGRATION STATUS");
    Console.WriteLine(new string('-', 60));

    // Rebuild slug lookups after downloads
    cachedSourceSlugs = sourcePosts.ToDictionary(p => p.Slug, p => p);
    var targetSlugsSet = targetPosts.Select(p => p.Slug).ToHashSet();
    var targetBaseSlugsSet = targetPosts.Select(p => GetBaseSlug(p.Slug)).ToHashSet();

    // Posts in source but not in target (need migration)
    var postsNeedingMigration = sourcePosts
        .Where(p => !targetSlugsSet.Contains(p.Slug) && !targetBaseSlugsSet.Contains(p.Slug))
        .ToList();

    // Posts in source that already exist in target
    var alreadyMigratedPosts = sourcePosts
        .Where(p => targetSlugsSet.Contains(p.Slug) || targetBaseSlugsSet.Contains(p.Slug))
        .ToList();

    Console.WriteLine($"\nSource posts: {sourcePosts.Count}");
    Console.WriteLine($"  Already migrated: {alreadyMigratedPosts.Count}");
    Console.WriteLine($"  Need migration: {postsNeedingMigration.Count}");

    if (postsNeedingMigration.Count > 0 && postsNeedingMigration.Count <= 20)
    {
        Console.WriteLine("\nPosts needing migration:");
        foreach (var post in postsNeedingMigration.OrderBy(p => p.Date))
        {
            Console.WriteLine($"  - {post.Slug} ({post.Date:yyyy-MM-dd})");
        }
    }
    else if (postsNeedingMigration.Count > 20)
    {
        Console.WriteLine($"\n(Showing first 20 of {postsNeedingMigration.Count} posts needing migration)");
        foreach (var post in postsNeedingMigration.OrderBy(p => p.Date).Take(20))
        {
            Console.WriteLine($"  - {post.Slug} ({post.Date:yyyy-MM-dd})");
        }
    }

    // Save detailed migration status
    var migrationStatus = new
    {
        GeneratedAt = DateTime.Now,
        TotalSourcePosts = sourcePosts.Count,
        TotalTargetPosts = targetPosts.Count,
        AlreadyMigrated = alreadyMigratedPosts.Select(p => new { p.Id, p.Slug, p.Date }).ToList(),
        NeedMigration = postsNeedingMigration.Select(p => new { p.Id, p.Slug, p.Date }).ToList()
    };
    await SaveToCache("migrationStatus.json", migrationStatus);
    Console.WriteLine($"\nMigration status saved to: migrationStatus.json");

    // ==========================================================================
    // REFRESH METADATA
    // ==========================================================================
    if (sourceTags.Count == 0 || sourceCategories.Count == 0 || sourceAuthors.Count == 0)
    {
        Console.WriteLine("\nDownloading source metadata...");
        sourceAuthors = (await sourceWp.Users.GetAllAsync()).ToList();
        sourceTags = (await sourceWp.Tags.GetAllAsync()).ToList();
        sourceCategories = (await sourceWp.Categories.GetAllAsync()).ToList();
    }

    if (targetTags.Count == 0 || targetCategories.Count == 0 || targetAuthors.Count == 0)
    {
        Console.WriteLine("Downloading target metadata...");
        try
        {
            targetAuthors = (await targetWp.Users.GetAllAsync()).ToList();
            targetTags = (await targetWp.Tags.GetAllAsync()).ToList();
            targetCategories = (await targetWp.Categories.GetAllAsync()).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  WordPressPCL failed: {ex.Message}");
            Console.WriteLine("  Falling back to direct API calls...");
            targetAuthors = await FetchAllFromApi<User>("users") ?? [];
            targetTags = await FetchAllFromApi<Tag>("tags") ?? [];
            targetCategories = await FetchAllFromApi<Category>("categories") ?? [];
        }
    }

    // Build lookup dictionaries
    BuildLookupDictionaries();

    Console.WriteLine("\n" + new string('=', 60));
    Console.WriteLine($"SUMMARY: {sourcePosts.Count} source posts, {targetPosts.Count} target posts");
    Console.WriteLine($"         {postsNeedingMigration.Count} posts ready for migration");
    Console.WriteLine(new string('=', 60));
}

async Task<List<T>?> FetchAllFromApi<T>(string endpoint)
{
    try
    {
        var request = CreateAuthenticatedRequest(HttpMethod.Get, $"{targetWpApiUrl}wp/v2/{endpoint}?per_page=100");
        var response = await httpClient.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  Error fetching {endpoint}: {json}");
            return null;
        }

        return JsonConvert.DeserializeObject<List<T>>(json);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error fetching {endpoint}: {ex.Message}");
        return null;
    }
}

async Task<HashSet<int>> GetAllPostIds(string apiUrl, bool useAuth = false)
{
    var allIds = new HashSet<int>();
    var page = 1;

    while (true)
    {
        try
        {
            var url = $"{apiUrl}wp/v2/posts?_fields=id&per_page=100&page={page}";
            Console.WriteLine($"  Fetching: {url}");

            HttpResponseMessage response;
            if (useAuth)
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
                response = await httpClient.SendAsync(request);
            }
            else
            {
                response = await httpClient.GetAsync(url);
            }

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"  Error: {response.StatusCode} - {json}");
                break;
            }

            var ids = JsonConvert.DeserializeObject<List<IdOnly>>(json) ?? [];
            Console.WriteLine($"  Found {ids.Count} posts on page {page}");

            if (ids.Count == 0) break;

            foreach (var id in ids)
                allIds.Add(id.Id);

            page++;
        }
        catch
        {
            break;
        }
    }

    return allIds;
}

void BuildLookupDictionaries()
{
    sourceTagsDict = sourceTags.ToDictionary(t => t.Id);
    sourceUsersDict = sourceAuthors.ToDictionary(u => u.Id);
    sourceCategoriesDict = sourceCategories.ToDictionary(c => c.Id);
    targetTagsDict = targetTags.ToDictionary(t => t.Slug);
    targetAuthorsDict = targetAuthors.ToDictionary(u => u.Slug);
    targetCategoriesDict = targetCategories.ToDictionary(c => c.Slug);
}

// =============================================================================
// DUPLICATE DETECTION AND CLEANUP
// =============================================================================

List<Post> FindDuplicates()
{
    Console.WriteLine("\n" + new string('=', 60));
    Console.WriteLine("FINDING DUPLICATES ON TARGET");
    Console.WriteLine(new string('=', 60));

    Console.WriteLine($"\nSource posts: {sourcePosts.Count}");
    Console.WriteLine($"Target posts in cache: {targetPosts.Count}");

    // Build lookup of target posts by slug (using cache)
    var targetPostsBySlug = targetPosts.ToDictionary(p => p.Slug, p => p);

    // Find duplicates: target posts where slug is {source_slug}-2, -3, etc. AND has EXACT same date+time
    var duplicates = new List<Post>();

    foreach (var sourcePost in sourcePosts)
    {
        // Check for -2, -3, ... -19 suffixes
        for (int suffix = 2; suffix <= 19; suffix++)
        {
            var potentialDuplicateSlug = $"{sourcePost.Slug}-{suffix}";

            if (targetPostsBySlug.TryGetValue(potentialDuplicateSlug, out var targetPost))
            {
                // Duplicates have IDENTICAL timestamps (same date AND time)
                if (targetPost.Date == sourcePost.Date)
                {
                    duplicates.Add(targetPost);
                    Console.WriteLine($"  {sourcePost.Slug} -> {targetPost.Slug} (Date: {sourcePost.Date:yyyy-MM-dd HH:mm:ss})");
                }
            }
        }
    }

    if (duplicates.Count == 0)
    {
        Console.WriteLine("\nNo duplicates found.");
    }
    else
    {
        Console.WriteLine($"\nFound {duplicates.Count} duplicate(s) to delete.");
    }

    return duplicates;
}

async Task DeleteDuplicatePosts(List<Post> duplicates)
{
    if (duplicates.Count == 0)
    {
        Console.WriteLine("\nNo duplicates to delete.");
        return;
    }

    Console.WriteLine($"\nDeleting {duplicates.Count} duplicate post(s)...");

    foreach (var post in duplicates)
    {
        try
        {
            Console.Write($"  Deleting post {post.Id} ({post.Slug})...");
            var success = await DeletePost(post.Id);
            if (success)
            {
                Console.WriteLine(" Done");
                targetPosts.RemoveAll(p => p.Id == post.Id);
            }
            else
            {
                Console.WriteLine(" Failed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Error: {ex.Message}");
        }
    }

    Console.WriteLine("Duplicate cleanup complete.");
}

async Task<List<PostSlugInfo>> FetchAllTargetPostsWithSlugs()
{
    var allPosts = new List<PostSlugInfo>();
    var page = 1;

    while (true)
    {
        try
        {
            var url = $"{targetWpApiUrl}wp/v2/posts?_fields=id,slug,date&per_page=100&page={page}";
            var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
            var response = await httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode) break;

            var posts = JsonConvert.DeserializeObject<List<PostSlugInfo>>(json) ?? [];
            if (posts.Count == 0) break;

            allPosts.AddRange(posts);
            page++;
        }
        catch
        {
            break;
        }
    }

    return allPosts;
}

async Task<bool> DeletePost(int postId)
{
    try
    {
        var url = $"{targetWpApiUrl}wp/v2/posts/{postId}?force=true";
        var request = CreateAuthenticatedRequest(HttpMethod.Delete, url);
        var response = await httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

async Task<PostSlugInfo?> CheckPostExistsBySlug(string slug)
{
    try
    {
        var url = $"{targetWpApiUrl}wp/v2/posts?slug={Uri.EscapeDataString(slug)}&_fields=id,slug";
        var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
        var response = await httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var posts = JsonConvert.DeserializeObject<List<PostSlugInfo>>(json);

        return posts?.FirstOrDefault();
    }
    catch
    {
        return null;
    }
}

// =============================================================================
// AUTHOR MIGRATION
// =============================================================================

async Task MigrateAuthors()
{
    Console.WriteLine("\nMigrating authors...");

    var authorsToCreate = new List<User>();

    foreach (var sourceAuthor in sourceAuthors)
    {
        var existingTarget = targetAuthors.FirstOrDefault(a =>
            a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
            a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));

        if (existingTarget == null)
        {
            authorsToCreate.Add(sourceAuthor);
        }
    }

    if (authorsToCreate.Count == 0)
    {
        Console.WriteLine("All authors already exist on target.");
        return;
    }

    Console.WriteLine($"Creating {authorsToCreate.Count} missing author(s)...");

    foreach (var sourceAuthor in authorsToCreate)
    {
        try
        {
            Console.Write($"  Creating author: {sourceAuthor.Name} ({sourceAuthor.Slug})...");

            var newAuthor = await CreateAuthorOnTarget(sourceAuthor);
            if (newAuthor != null)
            {
                targetAuthors.Add(newAuthor);
                targetAuthorsDict[newAuthor.Slug] = newAuthor;
                Console.WriteLine($" ID: {newAuthor.Id}");
            }
            else
            {
                Console.WriteLine(" Failed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Error: {ex.Message}");
        }
    }
}

async Task<User?> CreateAuthorOnTarget(User sourceAuthor)
{
    try
    {
        var url = $"{targetWpApiUrl}wp/v2/users";
        var request = CreateAuthenticatedRequest(HttpMethod.Post, url);

        // Generate a random password for the new user
        var randomPassword = System.Guid.NewGuid().ToString("N")[..16];

        var payload = new
        {
            username = sourceAuthor.Slug,
            name = sourceAuthor.Name,
            slug = sourceAuthor.Slug,
            email = $"{sourceAuthor.Slug}@holographica.space", // Generate email if not available
            password = randomPassword,
            roles = new[] { "author" },
            description = sourceAuthor.Description ?? ""
        };

        request.Content = new StringContent(
            JsonConvert.SerializeObject(payload),
            Encoding.UTF8,
            "application/json");

        var response = await httpClient.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // Check if user already exists (might have been created with different slug)
            if (json.Contains("existing_user_login") || json.Contains("existing_user_email"))
            {
                Console.Write(" (already exists, fetching)");
                // Refresh target authors and find the existing one
                var refreshedAuthors = await FetchAllFromApi<User>("users");
                if (refreshedAuthors != null)
                {
                    var existing = refreshedAuthors.FirstOrDefault(a =>
                        a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
                        a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));
                    return existing;
                }
            }
            Console.WriteLine($"\n    API Error: {json}");
            return null;
        }

        return JsonConvert.DeserializeObject<User>(json);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n    Exception: {ex.Message}");
        return null;
    }
}

async Task UpdateExistingPostsAuthors()
{
    Console.WriteLine("\nChecking existing posts for author mismatches...");

    var postsToUpdate = new List<(Post targetPost, int correctAuthorId, string authorName)>();

    // Build a mapping of source slug -> target post for existing posts
    var targetPostsBySlug = targetPosts.ToDictionary(p => p.Slug, p => p);

    foreach (var sourcePost in sourcePosts)
    {
        // Check if this post exists on target
        if (!targetPostsBySlug.TryGetValue(sourcePost.Slug, out var targetPost))
            continue;

        // Get the source author
        if (!sourceUsersDict.TryGetValue(sourcePost.Author, out var sourceAuthor))
            continue;

        // Find the matching target author
        var targetAuthor = targetAuthors.FirstOrDefault(a =>
            a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
            a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));

        if (targetAuthor == null)
        {
            Console.WriteLine($"  Warning: No target author found for '{sourceAuthor.Name}' (post: {sourcePost.Slug})");
            continue;
        }

        // Check if the author is already correct
        if (targetPost.Author == targetAuthor.Id)
            continue;

        postsToUpdate.Add((targetPost, targetAuthor.Id, targetAuthor.Name));
    }

    if (postsToUpdate.Count == 0)
    {
        Console.WriteLine("All existing posts have correct authors.");
        return;
    }

    Console.WriteLine($"Updating {postsToUpdate.Count} post(s) with correct authors...");

    foreach (var (targetPost, correctAuthorId, authorName) in postsToUpdate)
    {
        try
        {
            Console.Write($"  Updating '{targetPost.Slug}' -> author: {authorName}...");

            var success = await UpdatePostAuthor(targetPost.Id, correctAuthorId);
            if (success)
            {
                targetPost.Author = correctAuthorId;
                Console.WriteLine(" Done");
            }
            else
            {
                Console.WriteLine(" Failed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Error: {ex.Message}");
        }
    }
}

async Task<bool> UpdatePostAuthor(int postId, int authorId)
{
    try
    {
        var url = $"{targetWpApiUrl}wp/v2/posts/{postId}";
        var request = CreateAuthenticatedRequest(HttpMethod.Post, url);

        var payload = new { author = authorId };
        request.Content = new StringContent(
            JsonConvert.SerializeObject(payload),
            Encoding.UTF8,
            "application/json");

        var response = await httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.Write($" (API error: {error})");
            return false;
        }

        return true;
    }
    catch
    {
        return false;
    }
}

// =============================================================================
// POST PROCESSING
// =============================================================================

async Task ProcessPosts(int? limit = null, string? slug = null)
{
    Console.WriteLine("\nProcessing posts...");

    IEnumerable<Post> postsToProcess;

    if (!string.IsNullOrEmpty(slug))
    {
        postsToProcess = sourcePosts.Where(p => p.Slug == slug);
    }
    else
    {
        // Check exact slug match only
        var targetSlugs = targetPosts.Select(p => p.Slug).ToHashSet();

        Console.WriteLine($"  Source posts: {sourcePosts.Count}");
        Console.WriteLine($"  Target slugs in cache: {targetSlugs.Count}");

        postsToProcess = sourcePosts.Where(p => !targetSlugs.Contains(p.Slug));

        if (limit.HasValue)
            postsToProcess = postsToProcess.Take(limit.Value);
    }

    var postsList = postsToProcess.ToList();

    if (postsList.Count == 0)
    {
        Console.WriteLine("No posts to process - all posts already migrated.");
        return;
    }

    Console.WriteLine($"Processing {postsList.Count} post(s) that don't exist on target...\n");

    for (var i = 0; i < postsList.Count; i++)
    {
        var sourcePost = postsList[i];
        var progress = ((i + 1) / (decimal)postsList.Count * 100).ToString("F1");
        Console.WriteLine($"[{progress}%] Processing: {sourcePost.Slug}");

        try
        {
            await MigratePost(sourcePost);
            await SaveCache(); // Save after each successful migration
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }

        Console.WriteLine();
    }
}

async Task MigratePost(Post sourcePost)
{
    // 0. Check if post already exists on target (double-check via API)
    var existingPost = await CheckPostExistsBySlug(sourcePost.Slug);
    if (existingPost != null)
    {
        Console.WriteLine($"  Post already exists on target (ID: {existingPost.Id}), skipping.");
        // Make sure it's in our local cache
        if (!targetPosts.Any(p => p.Id == existingPost.Id))
        {
            try
            {
                var fullPost = await targetWp.Posts.GetByIDAsync(existingPost.Id);
                targetPosts.Add(fullPost);
            }
            catch
            {
                // Ignore - post exists but we couldn't fetch full details
            }
        }
        return;
    }

    // 1. Migrate tags
    var tagIds = new List<int>();
    foreach (var sourceTagId in sourcePost.Tags)
    {
        if (!sourceTagsDict.TryGetValue(sourceTagId, out var sourceTag)) continue;

        if (targetTagsDict.TryGetValue(sourceTag.Slug, out var targetTag))
        {
            tagIds.Add(targetTag.Id);
        }
        else
        {
            try
            {
                Console.WriteLine($"  Creating tag: {sourceTag.Name}");
                var newTag = await targetWp.Tags.CreateAsync(new Tag
                {
                    Name = sourceTag.Name,
                    Slug = sourceTag.Slug,
                    Description = sourceTag.Description
                });
                targetTags.Add(newTag);
                targetTagsDict[newTag.Slug] = newTag;
                tagIds.Add(newTag.Id);
            }
            catch
            {
                // Tag already exists, fetch it
                var existing = (await targetWp.Tags.GetAllAsync(useAuth: true))
                    .FirstOrDefault(t => t.Slug == sourceTag.Slug || t.Name == sourceTag.Name);
                if (existing != null)
                {
                    targetTagsDict[existing.Slug] = existing;
                    tagIds.Add(existing.Id);
                }
            }
        }
    }

    // 2. Migrate categories
    var categoryIds = new List<int>();
    foreach (var sourceCatId in sourcePost.Categories)
    {
        if (!sourceCategoriesDict.TryGetValue(sourceCatId, out var sourceCat)) continue;

        if (targetCategoriesDict.TryGetValue(sourceCat.Slug, out var targetCat))
        {
            categoryIds.Add(targetCat.Id);
        }
        else
        {
            try
            {
                Console.WriteLine($"  Creating category: {sourceCat.Name}");
                var newCat = await targetWp.Categories.CreateAsync(new Category
                {
                    Name = sourceCat.Name,
                    Slug = sourceCat.Slug,
                    Description = sourceCat.Description
                });
                targetCategories.Add(newCat);
                targetCategoriesDict[newCat.Slug] = newCat;
                categoryIds.Add(newCat.Id);
            }
            catch
            {
                // Category already exists, fetch it
                var existing = (await targetWp.Categories.GetAllAsync(useAuth: true))
                    .FirstOrDefault(c => c.Slug == sourceCat.Slug || c.Name == sourceCat.Name);
                if (existing != null)
                {
                    targetCategoriesDict[existing.Slug] = existing;
                    categoryIds.Add(existing.Id);
                }
            }
        }
    }

    // 3. Process content - migrate media (track uploaded media IDs for later attachment)
    var uploadedMediaIds = new List<int>();
    var content = sourcePost.Content.Rendered;
    content = await ProcessContentMedia(content, uploadedMediaIds);
    content = ProcessShortcodes(content);
    content = CleanupHtml(content);

    // 4. Map author (create if doesn't exist)
    var authorId = 1; // Default to admin
    if (sourceUsersDict.TryGetValue(sourcePost.Author, out var sourceAuthor))
    {
        var targetAuthor = targetAuthorsDict.Values.FirstOrDefault(a =>
            a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
            a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));

        if (targetAuthor != null)
        {
            authorId = targetAuthor.Id;
            Console.WriteLine($"  Author: {sourceAuthor.Name} -> {targetAuthor.Name}");
        }
        else
        {
            // Try to create the author
            Console.Write($"  Creating missing author: {sourceAuthor.Name}...");
            var newAuthor = await CreateAuthorOnTarget(sourceAuthor);
            if (newAuthor != null)
            {
                targetAuthors.Add(newAuthor);
                targetAuthorsDict[newAuthor.Slug] = newAuthor;
                authorId = newAuthor.Id;
                Console.WriteLine($" ID: {newAuthor.Id}");
            }
            else
            {
                Console.WriteLine($" Failed, using admin");
            }
        }
    }

    // 5. Upload featured image
    int? featuredMediaId = null;
    if (sourcePost.FeaturedMedia > 0)
    {
        var featuredUrl = await GetMediaUrl(sourcePost.FeaturedMedia.Value);
        if (featuredUrl != null)
        {
            var uploaded = await UploadMedia(featuredUrl);
            if (uploaded != null)
            {
                featuredMediaId = uploaded.Id;
            }
        }
        else
        {
            // Source media URL not resolvable - fall back to first content image
            Console.WriteLine($"  Source featured media not found, falling back to first content image...");
            var doc = new HtmlDocument();
            doc.LoadHtml(sourcePost.Content.Rendered);

            var firstMedia = doc.DocumentNode.SelectSingleNode("//img[@src] | //video");
            if (firstMedia != null && firstMedia.Name == "img")
            {
                var src = firstMedia.GetAttributeValue("src", "");
                if (!string.IsNullOrEmpty(src) && IsSourceMedia(src))
                {
                    var uploaded = await UploadMedia(src);
                    if (uploaded != null)
                    {
                        featuredMediaId = uploaded.Id;
                    }
                }
            }
        }
    }

    // 6. Get custom fields (meta) from source post - only post_views
    var allMeta = await GetPostMeta(sourcePost.Id);
    var customMeta = allMeta?
        .Where(kv => kv.Key.StartsWith("post_view"))
        .ToDictionary(kv => kv.Key, kv => kv.Value);

    // 7. Prepare excerpt
    var excerpt = sourcePost.Excerpt.Rendered;
    if (!string.IsNullOrEmpty(excerpt))
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(excerpt);
        excerpt = WebUtility.HtmlDecode(doc.DocumentNode.InnerText.Trim());
    }

    // 8. Create post on target
    var newPost = new Post
    {
        Title = new Title(WebUtility.HtmlDecode(sourcePost.Title.Rendered)),
        Slug = sourcePost.Slug,
        Content = new Content(content),
        Excerpt = new Excerpt(excerpt ?? ""),
        Status = sourcePost.Status,
        Date = sourcePost.Date,
        Tags = tagIds,
        Categories = categoryIds,
        Author = authorId,
        FeaturedMedia = featuredMediaId
    };

    var createdPost = await targetWp.Posts.CreateAsync(newPost);
    Console.WriteLine($"  Created post ID: {createdPost.Id}");

    // 9. Attach all uploaded media to this post (including featured image)
    if (featuredMediaId.HasValue)
    {
        uploadedMediaIds.Add(featuredMediaId.Value);
    }

    if (uploadedMediaIds.Count > 0)
    {
        // Remove duplicates in case featured image was also in content
        var uniqueMediaIds = uploadedMediaIds.Distinct().ToList();
        Console.WriteLine($"  Attaching {uniqueMediaIds.Count} media item(s) to post...");
        foreach (var mediaId in uniqueMediaIds)
        {
            await AttachMediaToPost(mediaId, createdPost.Id);
        }
    }

    // 10. Update custom fields on target post
    if (customMeta is { Count: > 0 })
    {
        Console.WriteLine($"  Custom fields: {string.Join(", ", customMeta.Keys)}");
        await UpdatePostMeta(createdPost.Id, customMeta);
        Console.WriteLine($"  Updated {customMeta.Count} custom field(s)");
    }

    targetPosts.Add(createdPost);
}

// =============================================================================
// CUSTOM FIELDS (META)
// =============================================================================

async Task<Dictionary<string, object>?> GetPostMeta(int postId)
{
    try
    {
        var url = $"{sourceWpApiUrl}wp/v2/posts/{postId}?_fields=meta";
        var response = await httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var result = JObject.Parse(json);
        return result["meta"]?.ToObject<Dictionary<string, object>>();
    }
    catch
    {
        return null;
    }
}

async Task UpdatePostMeta(int postId, Dictionary<string, object> meta)
{
    try
    {
        var url = $"{targetWpApiUrl}wp/v2/posts/{postId}";
        var request = CreateAuthenticatedRequest(HttpMethod.Post, url);

        var payload = new { meta };
        request.Content = new StringContent(
            JsonConvert.SerializeObject(payload),
            Encoding.UTF8,
            "application/json");

        var response = await httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"  Warning: Failed to update meta: {error}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Warning: Meta update error: {ex.Message}");
    }
}

// =============================================================================
// MEDIA HANDLING
// =============================================================================
async Task<string> ProcessContentMedia(string content, List<int>? uploadedMediaIds = null)
{
    var doc = new HtmlDocument();
    doc.LoadHtml(content);

    // Process images
    foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
    {
        var src = img.GetAttributeValue("src", "");
        if (string.IsNullOrEmpty(src) || !IsSourceMedia(src)) continue;

        var uploaded = await UploadMedia(src);
        if (uploaded == null) continue;

        img.SetAttributeValue("src", uploaded.SourceUrl);
        img.Attributes.Remove("srcset"); // Remove srcset to prevent broken responsive images
        uploadedMediaIds?.Add(uploaded.Id);
    }

    // Process video sources
    foreach (var source in doc.DocumentNode.SelectNodes("//video//source[@src] | //video[@src]") ?? Enumerable.Empty<HtmlNode>())
    {
        var src = source.GetAttributeValue("src", "");
        if (string.IsNullOrEmpty(src) || !IsSourceMedia(src)) continue;

        var uploaded = await UploadMedia(src);
        if (uploaded != null)
        {
            source.SetAttributeValue("src", uploaded.SourceUrl);
            uploadedMediaIds?.Add(uploaded.Id);
        }
    }

    // Process anchor links to media files
    foreach (var anchor in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
    {
        var href = anchor.GetAttributeValue("href", "");
        if (string.IsNullOrEmpty(href) || !IsSourceMedia(href)) continue;

        // Check if it's a document/file (not image/video which are handled above)
        var ext = Path.GetExtension(href).ToLower().Split('?')[0];
        if (ext is not (".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".zip" or ".rar")) continue;
        var uploaded = await UploadMedia(href);
        if (uploaded != null)
        {
            anchor.SetAttributeValue("href", uploaded.SourceUrl);
            uploadedMediaIds?.Add(uploaded.Id);
        }
    }

    return doc.DocumentNode.OuterHtml;
}

bool IsSourceMedia(string url)
{
    return url.Contains(sourceWpUrl) || url.Contains("holographica.space/wp-content/uploads");
}

async Task<string?> GetMediaUrl(int mediaId)
{
    try
    {
        var url = $"{sourceWpApiUrl}wp/v2/media/{mediaId}?_fields=source_url";
        var response = await httpClient.GetAsync(url);
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
async Task<MediaItem?> UploadMedia(string sourceUrl, int? attachToPostId = null)
{
    try
    {
        var uri = new Uri(sourceUrl);
        var fileName = Path.GetFileName(uri.LocalPath);
        var originalFileName = fileName;
        fileName = Regex.Replace(fileName, @"[^\u0000-\u007F]+", "");
        if (fileName.Length > 100)
        {
            var ext = Path.GetExtension(fileName);
            fileName = fileName[..(100 - ext.Length)] + ext;
        }

        // Check if media already exists on target (using pre-loaded media list)
        var targetUrl = sourceUrl.Replace(sourceWpUrl, targetWpUrl);

        // Try to find by exact URL first, then by filename
        MediaItem? existingMedia = null;
        if (targetMediaByUrl.TryGetValue(targetUrl, out var byUrl))
        {
            existingMedia = byUrl;
        }
        else if (targetMediaByFilename.TryGetValue(originalFileName, out var byFilename))
        {
            existingMedia = byFilename;
        }
        else if (targetMediaByFilename.TryGetValue(fileName, out var bySanitizedFilename))
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

        // Download from source
        using var downloadResponse = await httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead);
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

        // Upload to target with post attachment if specified
        var uploadUrl = $"{targetWpApiUrl}wp/v2/media";
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

        var response = await httpClient.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($" Upload failed");
            return null;
        }

        var result = JsonConvert.DeserializeObject<MediaItem>(json);
        Console.WriteLine($"\r  Uploaded: {fileName} -> ID {result?.Id}                    ");

        // Add to our lookup so we don't re-upload
        if (result != null && !string.IsNullOrEmpty(result.SourceUrl))
        {
            targetMediaByUrl[result.SourceUrl] = result;
            targetMediaByFilename[fileName] = result;
        }

        return result;
    }
    catch (Exception ex)
    {
        Console.WriteLine($" Error: {ex.Message}");
        return null;
    }
}

async Task AttachMediaToPost(int mediaId, int postId)
{
    try
    {
        var url = $"{targetWpApiUrl}wp/v2/media/{mediaId}";
        var request = CreateAuthenticatedRequest(HttpMethod.Post, url);

        var payload = new { post = postId };
        request.Content = new StringContent(
            JsonConvert.SerializeObject(payload),
            Encoding.UTF8,
            "application/json");

        var response = await httpClient.SendAsync(request);
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


static string GetBaseSlug(string slug)
{
    // Remove WordPress duplicate suffix (-2, -3, etc.) to get the base slug
    // WordPress only appends small numbers for duplicates, typically -2 through -9
    // We use -2 through -19 to be safe, but avoid stripping legitimate numbers like -2023, -100, etc.
    var match = System.Text.RegularExpressions.Regex.Match(slug, @"^(.+)-([2-9]|1[0-9])$");
    if (match.Success)
    {
        return match.Groups[1].Value;
    }
    return slug;
}

static string GetMimeType(string fileName)
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

// =============================================================================
// DIAGNOSTIC: FIND POSTS WITH MISSING FEATURED IMAGES
// =============================================================================

async Task FindPostsWithMissingFeaturedImages()
{
    Console.WriteLine("\n" + new string('=', 60));
    Console.WriteLine("CHECKING FOR MISSING FEATURED IMAGES");
    Console.WriteLine(new string('=', 60));

    var targetPostsBySlug = targetPosts.ToDictionary(p => p.Slug, p => p);

    var postsToCheck = new List<(Post sourcePost, Post targetPost)>();
    foreach (var sourcePost in sourcePosts)
    {
        if (targetPostsBySlug.TryGetValue(sourcePost.Slug, out var targetPost))
        {
            postsToCheck.Add((sourcePost, targetPost));
        }
    }

    Console.WriteLine($"Matched {postsToCheck.Count} source-target pairs.");

    // Fetch featured_media for all target posts
    var targetPostIds = postsToCheck.Select(p => p.targetPost.Id).ToList();
    var targetPostData = await BatchFetchTargetPostData(targetPostIds);
    Console.WriteLine($"Fetched data for {targetPostData.Count} target posts.");

    // Check which featured media IDs actually exist
    var featuredMediaIds = targetPostData.Values
        .Where(d => d.featuredMedia is > 0)
        .Select(d => d.featuredMedia!.Value)
        .Distinct()
        .ToList();
    var existingMediaIds = await BatchVerifyMediaExist(featuredMediaIds);

    var missing = new List<(string slug, int targetId, int? targetFeaturedMedia, int? sourceFeaturedMedia)>();

    foreach (var (sourcePost, targetPost) in postsToCheck)
    {
        if (!targetPostData.TryGetValue(targetPost.Id, out var postData))
        {
            missing.Add((sourcePost.Slug, targetPost.Id, null, sourcePost.FeaturedMedia));
            continue;
        }

        var featuredMediaMissing = postData.featuredMedia is null or 0
            || !existingMediaIds.Contains(postData.featuredMedia.Value);

        if (featuredMediaMissing)
        {
            missing.Add((sourcePost.Slug, targetPost.Id, postData.featuredMedia, sourcePost.FeaturedMedia));
        }
    }

    Console.WriteLine($"\n{missing.Count} posts with missing featured images:\n");
    foreach (var (slug, targetId, targetFM, sourceFM) in missing.OrderBy(m => m.slug))
    {
        Console.WriteLine($"  {slug}  (target ID: {targetId}, target featured_media: {targetFM ?? 0}, source featured_media: {sourceFM ?? 0})");
    }
}

// =============================================================================
// VERIFY AND FIX EXISTING POSTS MEDIA
// =============================================================================

async Task VerifyAndFixExistingPostsMedia()
{
    Console.WriteLine("\n" + new string('=', 60));
    Console.WriteLine("VERIFYING AND FIXING MEDIA FOR EXISTING POSTS");
    Console.WriteLine(new string('=', 60));

    // Build lookup for matching source posts to target posts
    var targetPostsBySlug = targetPosts.ToDictionary(p => p.Slug, p => p);
    var targetPostsByBaseSlug = targetPosts
        .GroupBy(p => GetBaseSlug(p.Slug))
        .ToDictionary(g => g.Key, g => g.First());

    var postsToFix = new List<(Post sourcePost, Post targetPost)>();

    foreach (var sourcePost in sourcePosts)
    {
        // Find matching target post (by exact slug or base slug)
        Post? targetPost = null;
        if (targetPostsBySlug.TryGetValue(sourcePost.Slug, out var exactMatch))
        {
            targetPost = exactMatch;
        }
        else if (targetPostsByBaseSlug.TryGetValue(sourcePost.Slug, out var baseMatch))
        {
            targetPost = baseMatch;
        }
        else if (targetPostsByBaseSlug.TryGetValue(GetBaseSlug(sourcePost.Slug), out var baseMatch2))
        {
            targetPost = baseMatch2;
        }

        if (targetPost != null)
        {
            postsToFix.Add((sourcePost, targetPost));
        }
    }

    Console.WriteLine($"\nFound {postsToFix.Count} source-target post pairs to verify.");

    // Batch fetch all target post data (featured_media + content) in one go
    Console.WriteLine("Fetching target post data in bulk...");
    var targetPostIds = postsToFix.Select(p => p.targetPost.Id).ToList();
    var targetPostData = await BatchFetchTargetPostData(targetPostIds);
    Console.WriteLine($"  Fetched data for {targetPostData.Count} posts.");

    // Batch verify which featured media IDs actually exist
    var featuredMediaIds = targetPostData.Values
        .Where(d => d.featuredMedia is > 0)
        .Select(d => d.featuredMedia!.Value)
        .Distinct()
        .ToList();
    Console.WriteLine($"Verifying {featuredMediaIds.Count} featured media items...");
    var existingMediaIds = await BatchVerifyMediaExist(featuredMediaIds);
    Console.WriteLine($"  {existingMediaIds.Count} exist, {featuredMediaIds.Count - existingMediaIds.Count} missing.");

    var fixedFeaturedImages = 0;
    var fixedMediaAttachments = 0;

    for (var i = 0; i < postsToFix.Count; i++)
    {
        var (sourcePost, targetPost) = postsToFix[i];
        var progress = ((i + 1) / (decimal)postsToFix.Count * 100).ToString("F1");
        var hasIssues = false;

        // Get pre-fetched data for this target post
        if (!targetPostData.TryGetValue(targetPost.Id, out var postData))
            continue;

        // Check 1: Featured image
        {
            var featuredMediaMissing = postData.featuredMedia is null or 0
                || !existingMediaIds.Contains(postData.featuredMedia.Value);

            if (featuredMediaMissing)
            {
                if (sourcePost.FeaturedMedia > 0)
                {
                    // Source had an explicit featured image - upload it
                    if (!hasIssues)
                    {
                        Console.WriteLine($"\n[{progress}%] Fixing: {sourcePost.Slug}");
                        hasIssues = true;
                    }

                    Console.WriteLine($"  Featured image missing, uploading...");
                    var featuredUrl = await GetMediaUrl(sourcePost.FeaturedMedia.Value);
                    if (featuredUrl != null)
                    {
                        var uploaded = await UploadMedia(featuredUrl, targetPost.Id);
                        if (uploaded != null)
                        {
                            await SetPostFeaturedImage(targetPost.Id, uploaded.Id);
                            Console.WriteLine($"  Set featured image ID: {uploaded.Id}");
                            fixedFeaturedImages++;
                        }
                        await Task.Delay(1000); // Throttle to avoid overwhelming server
                    }
                    else
                    {
                        // Source media URL not resolvable - fall back to first content image or video
                        if (!hasIssues) { Console.WriteLine($"\n[{progress}%] Fixing: {sourcePost.Slug}"); hasIssues = true; }
                        Console.WriteLine($"  Source media not found, falling back to content...");
                        if (await TrySetFeaturedFromContent(sourcePost, targetPost)) fixedFeaturedImages++;
                        await Task.Delay(1000); // Throttle to avoid overwhelming server
                    }
                }
                else
                {
                    // Source had no explicit featured image - use first image or video from content
                    if (!hasIssues) { Console.WriteLine($"\n[{progress}%] Fixing: {sourcePost.Slug}"); hasIssues = true; }
                    if (await TrySetFeaturedFromContent(sourcePost, targetPost)) fixedFeaturedImages++;
                    await Task.Delay(1000); // Throttle to avoid overwhelming server
                }
            }
        }

        // Check 2: Content media - check if media URLs in target content exist
        var targetContent = postData.content;
        if (string.IsNullOrEmpty(targetContent))
        {
            continue; // Skip if we can't get content
        }

        var targetMediaUrls = ExtractMediaUrls(targetContent);
        var urlReplacements = new Dictionary<string, string>(); // old URL -> new URL

        // Also get source media URLs for finding original files
        var sourceMediaUrls = ExtractMediaUrls(sourcePost.Content.Rendered);
        var sourceUrlsByFilename = sourceMediaUrls
            .Where(u => IsSourceMedia(u))
            .ToDictionary(
                u => Path.GetFileName(new Uri(u).LocalPath),
                u => u,
                StringComparer.OrdinalIgnoreCase);

        foreach (var targetMediaUrl in targetMediaUrls)
        {
            // Only process URLs from our target domain
            if (!targetMediaUrl.Contains(targetWpUrl) && !targetMediaUrl.Contains("new.holographica.space"))
                continue;

            var fileName = Path.GetFileName(new Uri(targetMediaUrl).LocalPath);

            // Check if this media URL exists in our pre-loaded target media list
            var mediaExists = targetMediaByUrl.ContainsKey(targetMediaUrl) ||
                              targetMediaByFilename.ContainsKey(fileName);

            if (!mediaExists)
            {
                if (!hasIssues)
                {
                    Console.WriteLine($"\n[{progress}%] Fixing: {sourcePost.Slug}");
                    hasIssues = true;
                }

                Console.WriteLine($"  Missing media: {fileName}");

                // Find the original source URL by filename
                if (sourceUrlsByFilename.TryGetValue(fileName, out var sourceUrl))
                {
                    // Upload from source and attach to post
                    var uploaded = await UploadMedia(sourceUrl, targetPost.Id);
                    if (uploaded != null)
                    {
                        fixedMediaAttachments++;
                        // Track URL replacement - replace broken target URL with new actual URL
                        if (!string.IsNullOrEmpty(uploaded.SourceUrl) &&
                            !uploaded.SourceUrl.Equals(targetMediaUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            urlReplacements[targetMediaUrl] = uploaded.SourceUrl;
                        }
                    }
                    await Task.Delay(1000); // Throttle to avoid overwhelming server
                }
                else
                {
                    Console.WriteLine($"    Could not find source file for: {fileName}");
                }
            }
        }

        // Update post content if we have URL replacements
        if (urlReplacements.Count > 0)
        {
            Console.WriteLine($"  Updating {urlReplacements.Count} URL(s) in post content...");
            await UpdatePostContentUrls(targetPost.Id, urlReplacements);
        }
    }

    Console.WriteLine($"\n" + new string('-', 60));
    Console.WriteLine($"Media verification complete:");
    Console.WriteLine($"  Fixed featured images: {fixedFeaturedImages}");
    Console.WriteLine($"  Fixed media attachments: {fixedMediaAttachments}");
    Console.WriteLine(new string('-', 60));
}

List<string> ExtractMediaUrls(string content)
{
    var urls = new List<string>();
    var doc = new HtmlDocument();
    doc.LoadHtml(content);

    // Extract image URLs
    foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
    {
        var src = img.GetAttributeValue("src", "");
        if (!string.IsNullOrEmpty(src))
            urls.Add(src);
    }

    // Extract video URLs
    foreach (var source in doc.DocumentNode.SelectNodes("//video//source[@src] | //video[@src]") ?? Enumerable.Empty<HtmlNode>())
    {
        var src = source.GetAttributeValue("src", "");
        if (!string.IsNullOrEmpty(src))
            urls.Add(src);
    }

    // Extract document links
    foreach (var anchor in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
    {
        var href = anchor.GetAttributeValue("href", "");
        if (string.IsNullOrEmpty(href)) continue;

        var ext = Path.GetExtension(href).ToLower().Split('?')[0];
        if (ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".zip" or ".rar")
        {
            urls.Add(href);
        }
    }

    return urls.Distinct().ToList();
}

async Task<Dictionary<int, (int? featuredMedia, string? content)>> BatchFetchTargetPostData(List<int> postIds)
{
    var result = new Dictionary<int, (int? featuredMedia, string? content)>();

    // WP REST API supports up to 100 IDs per request
    foreach (var batch in postIds.Chunk(100))
    {
        try
        {
            var ids = string.Join(",", batch);
            var url = $"{targetWpApiUrl}wp/v2/posts?include={ids}&_fields=id,featured_media,content&per_page={batch.Length}";
            var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
            var response = await httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode) continue;

            var json = await response.Content.ReadAsStringAsync();
            var posts = JArray.Parse(json);
            foreach (var post in posts)
            {
                var id = post["id"]!.Value<int>();
                var featuredMedia = post["featured_media"]?.Value<int?>();
                var content = post["content"]?["rendered"]?.ToString();
                result[id] = (featuredMedia, content);
            }
        }
        catch
        {
            // Fall back to individual fetches for this batch
            foreach (var id in batch)
            {
                try
                {
                    var url = $"{targetWpApiUrl}wp/v2/posts/{id}?_fields=id,featured_media,content";
                    var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
                    var response = await httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode) continue;

                    var json = await response.Content.ReadAsStringAsync();
                    var post = JObject.Parse(json);
                    var featuredMedia = post["featured_media"]?.Value<int?>();
                    var content = post["content"]?["rendered"]?.ToString();
                    result[id] = (featuredMedia, content);
                }
                catch { /* skip */ }
            }
        }
    }

    return result;
}

async Task<HashSet<int>> BatchVerifyMediaExist(List<int> mediaIds)
{
    var existing = new HashSet<int>();

    foreach (var batch in mediaIds.Distinct().Chunk(100))
    {
        try
        {
            var ids = string.Join(",", batch);
            var url = $"{targetWpApiUrl}wp/v2/media?include={ids}&_fields=id&per_page={batch.Length}";
            var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
            var response = await httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode) continue;

            var json = await response.Content.ReadAsStringAsync();
            var items = JArray.Parse(json);
            foreach (var item in items)
            {
                existing.Add(item["id"]!.Value<int>());
            }
        }
        catch { /* skip batch */ }
    }

    return existing;
}

async Task<bool> TrySetFeaturedFromContent(Post sourcePost, Post targetPost)
{
    var doc = new HtmlDocument();
    doc.LoadHtml(sourcePost.Content.Rendered);

    // Try first image from content
    var firstImg = doc.DocumentNode.SelectSingleNode("//img[@src]");
    if (firstImg != null)
    {
        var src = firstImg.GetAttributeValue("src", "");
        if (!string.IsNullOrEmpty(src) && IsSourceMedia(src))
        {
            Console.WriteLine($"  Using first content image...");
            var uploaded = await UploadMedia(src, targetPost.Id);
            if (uploaded != null)
            {
                await SetPostFeaturedImage(targetPost.Id, uploaded.Id);
                Console.WriteLine($"  Set featured image ID: {uploaded.Id}");
                return true;
            }
        }
    }

    // No image found - try to extract video URL from iframe (YouTube/Vimeo)
    var iframe = doc.DocumentNode.SelectSingleNode("//iframe[@src]");
    if (iframe != null)
    {
        var iframeSrc = iframe.GetAttributeValue("src", "");
        var videoUrl = ExtractVideoUrl(iframeSrc);
        if (videoUrl != null)
        {
            Console.WriteLine($"  No image, setting featured video: {videoUrl}");
            await SetPostFeaturedVideo(targetPost.Id, videoUrl);
            return true;
        }
    }

    return false;
}

string? ExtractVideoUrl(string iframeSrc)
{
    // YouTube embed URL -> watch URL
    var ytMatch = Regex.Match(iframeSrc, @"youtube\.com/embed/([a-zA-Z0-9_-]+)");
    if (ytMatch.Success)
        return $"https://www.youtube.com/watch?v={ytMatch.Groups[1].Value}";

    // Vimeo embed URL -> vimeo URL
    var vimeoMatch = Regex.Match(iframeSrc, @"player\.vimeo\.com/video/(\d+)");
    if (vimeoMatch.Success)
        return $"https://vimeo.com/{vimeoMatch.Groups[1].Value}";

    // Dailymotion embed URL
    var dmMatch = Regex.Match(iframeSrc, @"dailymotion\.com/embed/video/([a-zA-Z0-9]+)");
    if (dmMatch.Success)
        return $"https://www.dailymotion.com/video/{dmMatch.Groups[1].Value}";

    return null;
}

async Task SetPostFeaturedVideo(int postId, string videoUrl)
{
    try
    {
        var url = $"{targetWpApiUrl}wp/v2/posts/{postId}";
        var request = CreateAuthenticatedRequest(HttpMethod.Post, url);

        // tagDiv theme stores video as PHP-serialized array: a:1:{s:8:"td_video";s:LENGTH:"URL";}
        var serialized = $"a:1:{{s:8:\"td_video\";s:{videoUrl.Length}:\"{videoUrl}\";}}";

        // Don't set format to "video" — it causes 504 on the target site's theme
        var payload = new
        {
            meta = new Dictionary<string, object>
            {
                ["td_post_video"] = new[] { serialized },
                ["td_post_video_duration"] = new[] { "" }
            }
        };
        request.Content = new StringContent(
            JsonConvert.SerializeObject(payload),
            Encoding.UTF8,
            "application/json");

        var response = await httpClient.SendAsync(request);
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

async Task SetPostFeaturedImage(int postId, int mediaId)
{
    try
    {
        var url = $"{targetWpApiUrl}wp/v2/posts/{postId}";
        var request = CreateAuthenticatedRequest(HttpMethod.Post, url);

        var payload = new { featured_media = mediaId };
        request.Content = new StringContent(
            JsonConvert.SerializeObject(payload),
            Encoding.UTF8,
            "application/json");

        var response = await httpClient.SendAsync(request);
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

async Task UpdatePostContentUrls(int postId, Dictionary<string, string> urlReplacements)
{
    try
    {
        // First, get the current post content
        var getUrl = $"{targetWpApiUrl}wp/v2/posts/{postId}?_fields=content";
        var getRequest = CreateAuthenticatedRequest(HttpMethod.Get, getUrl);
        var getResponse = await httpClient.SendAsync(getRequest);

        if (!getResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"  Warning: Failed to get post content for URL update");
            return;
        }

        var json = await getResponse.Content.ReadAsStringAsync();
        var postData = JObject.Parse(json);
        var content = postData["content"]?["rendered"]?.ToString();

        if (string.IsNullOrEmpty(content))
        {
            Console.WriteLine($"  Warning: Post content is empty");
            return;
        }

        // Replace all old URLs with new URLs
        var updatedContent = content;
        foreach (var (oldUrl, newUrl) in urlReplacements)
        {
            updatedContent = updatedContent.Replace(oldUrl, newUrl);
            // Also try without protocol to catch mixed http/https
            var oldPath = oldUrl.Replace("https://", "").Replace("http://", "");
            var newPath = newUrl.Replace("https://", "").Replace("http://", "");
            updatedContent = updatedContent.Replace(oldPath, newPath);
        }

        // Only update if content actually changed
        if (updatedContent == content)
        {
            Console.WriteLine($"  No URL changes needed in content");
            return;
        }

        // Update the post with new content
        var updateUrl = $"{targetWpApiUrl}wp/v2/posts/{postId}";
        var updateRequest = CreateAuthenticatedRequest(HttpMethod.Post, updateUrl);

        var payload = new { content = updatedContent };
        updateRequest.Content = new StringContent(
            JsonConvert.SerializeObject(payload),
            Encoding.UTF8,
            "application/json");

        var updateResponse = await httpClient.SendAsync(updateRequest);
        if (!updateResponse.IsSuccessStatusCode)
        {
            var error = await updateResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"  Warning: Failed to update post content: {error}");
        }
        else
        {
            Console.WriteLine($"  Post content updated with new URLs");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Warning: Error updating post content: {ex.Message}");
    }
}

// =============================================================================
// CONTENT PROCESSING
// =============================================================================

string ProcessShortcodes(string content)
{
    // Process [embed] shortcodes for YouTube/Vimeo
    content = Regex.Replace(content, @"\[embed\](https?://(?:www\.)?youtube\.com/watch\?v=([a-zA-Z0-9_-]+)[^\[]*)\[/embed\]",
        m => $@"<figure class=""wp-block-embed is-type-video is-provider-youtube""><div class=""wp-block-embed__wrapper""><iframe width=""560"" height=""315"" src=""https://www.youtube.com/embed/{m.Groups[2].Value}"" frameborder=""0"" allowfullscreen></iframe></div></figure>",
        RegexOptions.IgnoreCase);

    content = Regex.Replace(content, @"\[embed\](https?://(?:www\.)?youtu\.be/([a-zA-Z0-9_-]+)[^\[]*)\[/embed\]",
        m => $@"<figure class=""wp-block-embed is-type-video is-provider-youtube""><div class=""wp-block-embed__wrapper""><iframe width=""560"" height=""315"" src=""https://www.youtube.com/embed/{m.Groups[2].Value}"" frameborder=""0"" allowfullscreen></iframe></div></figure>",
        RegexOptions.IgnoreCase);

    content = Regex.Replace(content, @"\[embed\](https?://(?:www\.)?vimeo\.com/(\d+)[^\[]*)\[/embed\]",
        m => $@"<figure class=""wp-block-embed is-type-video is-provider-vimeo""><div class=""wp-block-embed__wrapper""><iframe src=""https://player.vimeo.com/video/{m.Groups[2].Value}"" width=""560"" height=""315"" frameborder=""0"" allowfullscreen></iframe></div></figure>",
        RegexOptions.IgnoreCase);

    // Remove TablePress shortcodes (tables should already be rendered in content)
    content = Regex.Replace(content, @"\[/?table(?:press)?[^\]]*\]", "", RegexOptions.IgnoreCase);

    // Process [video] shortcodes
    content = Regex.Replace(content, @"\[video\s+[^\]]*(?:mp4|src)=[""']([^""']+)[""'][^\]]*\](?:\[/video\])?",
        m => $@"<figure class=""wp-block-video""><video controls src=""{m.Groups[1].Value}""></video></figure>",
        RegexOptions.IgnoreCase);

    return content;
}

string CleanupHtml(string content)
{
    var doc = new HtmlDocument();
    doc.LoadHtml(content);

    // Clean up TablePress tables
    foreach (var table in doc.DocumentNode.SelectNodes("//table[contains(@class, 'tablepress')]") ?? Enumerable.Empty<HtmlNode>())
    {
        // Remove TablePress-specific classes but keep the table
        var currentClass = table.GetAttributeValue("class", "");
        var newClass = Regex.Replace(currentClass, @"tablepress\s*tablepress-id-\d+\s*", "").Trim();
        if (string.IsNullOrEmpty(newClass))
        {
            table.Attributes.Remove("class");
        }
        else
        {
            table.SetAttributeValue("class", newClass);
        }

        // Remove data attributes
        foreach (var attr in table.Attributes.Where(a => a.Name.StartsWith("data-")).ToList())
        {
            table.Attributes.Remove(attr);
        }
    }

    // Remove TablePress wrapper divs
    foreach (var wrapper in (doc.DocumentNode.SelectNodes("//div[contains(@class, 'tablepress-scroll-wrapper')]") ?? Enumerable.Empty<HtmlNode>()).ToList())
    {
        var innerContent = wrapper.InnerHtml;
        var textNode = HtmlNode.CreateNode(innerContent);
        wrapper.ParentNode.ReplaceChild(textNode, wrapper);
    }

    // Wrap standalone iframes in figure tags
    foreach (var iframe in (doc.DocumentNode.SelectNodes("//iframe[not(ancestor::figure)]") ?? Enumerable.Empty<HtmlNode>()).ToList())
    {
        var figure = doc.CreateElement("figure");
        figure.SetAttributeValue("class", "wp-block-embed");
        var wrapperDiv = doc.CreateElement("div");
        wrapperDiv.SetAttributeValue("class", "wp-block-embed__wrapper");
        wrapperDiv.InnerHtml = iframe.OuterHtml;
        figure.AppendChild(wrapperDiv);
        iframe.ParentNode?.ReplaceChild(figure, iframe);
    }

    return doc.DocumentNode.OuterHtml;
}

// =============================================================================
// =============================================================================
// MODELS
// =============================================================================

class IdOnly
{
    [JsonProperty("id")] public int Id { get; set; }
}

class PostSlugInfo
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("date")] public DateTime Date { get; set; }
}

class PostFeaturedMediaInfo
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("featured_media")] public int? FeaturedMedia { get; set; }
}

public class MediaItem
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("source_url")] public string SourceUrl { get; set; } = "";
}

// =============================================================================
// HTTP RETRY HANDLER
// =============================================================================

public class RetryHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    public int MaxRetries { get; init; } = 3;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        for (var i = 0; i <= MaxRetries; i++)
        {
            try
            {
                // Clone the request for retries (request can only be sent once)
                var clonedRequest = await CloneRequest(request);
                response = await base.SendAsync(clonedRequest, cancellationToken);

                if (response.IsSuccessStatusCode)
                    return response;

                // Retry on server errors (5xx) and rate limiting (429)
                if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests) return response;
                if (i >= MaxRetries) return response;
                var delay = (i + 1) * 2000; // 2s, 4s, 6s
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException) when (i < MaxRetries)
            {
                var delay = (i + 1) * 2000;
                await Task.Delay(delay, cancellationToken);
            }
        }

        return response ?? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
    }

    private static async Task<HttpRequestMessage> CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        if (request.Content != null)
        {
            var content = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(content);

            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
