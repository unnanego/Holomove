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

// Media upload cache (source URL -> target MediaItem) to avoid re-uploading
var mediaCache = new Dictionary<string, MediaCacheItem>();

// =============================================================================
// MAIN EXECUTION
// =============================================================================

await AuthenticateTargetWp();
await LoadCache();
await DownloadAllPostsAndCompare();
await DeleteDuplicatePosts();
await MigrateAuthors();
await UpdateExistingPostsAuthors();
await ProcessPosts();
await SaveCache();
await GenerateRedirects();

Console.WriteLine("\nMigration complete!");
return;

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
    mediaCache = await LoadFromCache<Dictionary<string, MediaCacheItem>>("mediaCache.json") ?? [];

    Console.WriteLine($"  Source: {sourcePosts.Count} posts, {sourceTags.Count} tags, {sourceCategories.Count} categories");
    Console.WriteLine($"  Target: {targetPosts.Count} posts, {targetTags.Count} tags, {targetCategories.Count} categories");
    Console.WriteLine($"  Media cache: {mediaCache.Count} items");
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
    await SaveToCache("mediaCache.json", mediaCache);
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
    var apiSourceIds = await GetAllPostIds(sourceWpApiUrl);
    var apiTargetIds = await GetAllPostIds(targetWpApiUrl);

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
        foreach (var (id, index) in missingSourceIds.Select((id, i) => (id, i)))
        {
            Console.Write($"\r  Progress: {index + 1}/{missingSourceIds.Count} (ID: {id})          ");
            try
            {
                var post = await sourceWp.Posts.GetByIDAsync(id);
                sourcePosts.Add(post);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  Failed to download source post {id}: {ex.Message}");
            }
        }
        Console.WriteLine();
    }

    if (missingTargetIds.Count > 0)
    {
        Console.WriteLine($"\nDownloading {missingTargetIds.Count} missing target posts...");
        foreach (var (id, index) in missingTargetIds.Select((id, i) => (id, i)))
        {
            Console.Write($"\r  Progress: {index + 1}/{missingTargetIds.Count} (ID: {id})          ");
            try
            {
                var post = await targetWp.Posts.GetByIDAsync(id);
                targetPosts.Add(post);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  Failed to download target post {id}: {ex.Message}");
            }
        }
        Console.WriteLine();
    }

    // ==========================================================================
    // DUPLICATE DETECTION: Find posts with same slug
    // ==========================================================================
    Console.WriteLine("\n" + new string('-', 60));
    Console.WriteLine("DUPLICATE DETECTION");
    Console.WriteLine(new string('-', 60));

    // Find duplicate slugs in source
    var sourceSlugGroups = sourcePosts
        .GroupBy(p => GetBaseSlug(p.Slug))
        .Where(g => g.Count() > 1)
        .ToList();

    if (sourceSlugGroups.Count > 0)
    {
        Console.WriteLine($"\nSource duplicates (same base slug): {sourceSlugGroups.Count} groups");
        foreach (var group in sourceSlugGroups)
        {
            Console.WriteLine($"  '{group.Key}': {string.Join(", ", group.Select(p => $"ID {p.Id} ({p.Slug})"))}");
        }
    }
    else
    {
        Console.WriteLine("\nNo duplicate slugs in source.");
    }

    // Find duplicate slugs in target
    var targetSlugGroups = targetPosts
        .GroupBy(p => GetBaseSlug(p.Slug))
        .Where(g => g.Count() > 1)
        .ToList();

    if (targetSlugGroups.Count > 0)
    {
        Console.WriteLine($"\nTarget duplicates (same base slug): {targetSlugGroups.Count} groups");
        foreach (var group in targetSlugGroups)
        {
            Console.WriteLine($"  '{group.Key}': {string.Join(", ", group.Select(p => $"ID {p.Id} ({p.Slug})"))}");
        }
    }
    else
    {
        Console.WriteLine("\nNo duplicate slugs in target.");
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
        NeedMigration = postsNeedingMigration.Select(p => new { p.Id, p.Slug, p.Date }).ToList(),
        SourceDuplicates = sourceSlugGroups.Select(g => new
        {
            BaseSlug = g.Key,
            Posts = g.Select(p => new { p.Id, p.Slug, p.Date }).ToList()
        }).ToList(),
        TargetDuplicates = targetSlugGroups.Select(g => new
        {
            BaseSlug = g.Key,
            Posts = g.Select(p => new { p.Id, p.Slug, p.Date }).ToList()
        }).ToList()
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

async Task<HashSet<int>> GetAllPostIds(string apiUrl)
{
    var allIds = new HashSet<int>();
    var page = 1;

    while (true)
    {
        try
        {
            var url = $"{apiUrl}wp/v2/posts?_fields=id&per_page=100&page={page}";
            Console.WriteLine($"  Fetching: {url}");
            var response = await httpClient.GetAsync(url);

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

async Task DeleteDuplicatePosts()
{
    Console.WriteLine("\nChecking for duplicate posts on target...");

    // Fetch all posts from target API with slug info
    var allTargetPosts = await FetchAllTargetPostsWithSlugs();

    if (allTargetPosts.Count == 0)
    {
        Console.WriteLine("No posts found on target.");
        return;
    }

    // Group by base slug AND date to find duplicates (WordPress appends -2, -3, etc. to duplicate slugs)
    // Duplicates will have the same date since they were migrated from the same source post
    var duplicateGroups = allTargetPosts
        .GroupBy(p => (BaseSlug: GetBaseSlug(p.Slug), p.Date))
        .Where(g => g.Count() > 1)
        .ToList();

    if (duplicateGroups.Count == 0)
    {
        Console.WriteLine("No duplicate posts found.");
        return;
    }

    Console.WriteLine($"Found {duplicateGroups.Count} slug(s) with duplicates:");

    var postsToDelete = new List<(int Id, string Slug)>();

    foreach (var group in duplicateGroups)
    {
        var posts = group.OrderBy(p => p.Id).ToList(); // Keep the oldest (lowest ID)
        var keepPost = posts.First();
        var duplicates = posts.Skip(1).ToList();

        Console.WriteLine($"  '{group.Key.BaseSlug}' ({group.Key.Date:yyyy-MM-dd}): keeping ID {keepPost.Id}, deleting {string.Join(", ", duplicates.Select(p => p.Id))}");

        foreach (var dup in duplicates)
        {
            postsToDelete.Add((dup.Id, dup.Slug));
        }
    }

    if (postsToDelete.Count == 0) return;

    Console.WriteLine($"\nDeleting {postsToDelete.Count} duplicate post(s)...");

    foreach (var (id, slug) in postsToDelete)
    {
        try
        {
            Console.Write($"  Deleting post {id} ({slug})...");
            var success = await DeletePost(id);
            if (success)
            {
                Console.WriteLine(" Done");
                // Remove from local cache
                targetPosts.RemoveAll(p => p.Id == id);
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
            var url = $"{targetWpApiUrl}wp/v2/posts?_fields=id,slug&per_page=100&page={page}";
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
        var targetSlugs = targetPosts.Select(p => p.Slug).ToHashSet();
        postsToProcess = sourcePosts.Where(p => !targetSlugs.Contains(p.Slug));

        if (limit.HasValue)
            postsToProcess = postsToProcess.Take(limit.Value);
    }

    var postsList = postsToProcess.ToList();

    if (postsList.Count == 0)
    {
        Console.WriteLine("No posts to process.");
        return;
    }

    Console.WriteLine($"Processing {postsList.Count} post(s)...\n");

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
            var fullPost = await targetWp.Posts.GetByIDAsync(existingPost.Id);
            targetPosts.Add(fullPost);
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

    // 3. Process content - migrate media
    var content = sourcePost.Content.Rendered;
    content = await ProcessContentMedia(content);
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

    // 9. Update custom fields on target post
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
async Task<string> ProcessContentMedia(string content)
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
async Task<MediaItem?> UploadMedia(string sourceUrl)
{
    // Check cache first
    if (mediaCache.TryGetValue(sourceUrl, out var cached))
    {
        Console.WriteLine($"  [Cached] {Path.GetFileName(new Uri(sourceUrl).LocalPath)}");
        return new MediaItem { Id = cached.Id, SourceUrl = cached.Url };
    }

    try
    {
        var uri = new Uri(sourceUrl);
        var fileName = Path.GetFileName(uri.LocalPath);
        fileName = Regex.Replace(fileName, @"[^\u0000-\u007F]+", "");
        if (fileName.Length > 100)
        {
            var ext = Path.GetExtension(fileName);
            fileName = fileName[..(100 - ext.Length)] + ext;
        }

        Console.Write($"  Downloading: {fileName}...");

        // Download
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

        // Upload to target
        var uploadUrl = $"{targetWpApiUrl}wp/v2/media";
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

        // Cache the result
        if (result != null)
        {
            mediaCache[sourceUrl] = new MediaCacheItem { Id = result.Id, Url = result.SourceUrl };
        }

        return result;
    }
    catch (Exception ex)
    {
        Console.WriteLine($" Error: {ex.Message}");
        return null;
    }
}

static string GetBaseSlug(string slug)
{
    // Remove WordPress duplicate suffix (-2, -3, etc.) to get the base slug
    var match = System.Text.RegularExpressions.Regex.Match(slug, @"^(.+)-(\d+)$");
    if (match.Success && int.TryParse(match.Groups[2].Value, out var num) && num >= 2)
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
// REDIRECTS
// =============================================================================

async Task GenerateRedirects()
{
    Console.WriteLine("\nGenerating redirects...");

    var redirects = new List<object>();

    foreach (var post in sourcePosts)
    {
        var categoryId = post.Categories.FirstOrDefault();
        var categorySlug = "uncategorized";

        if (categoryId > 0 && sourceCategoriesDict.TryGetValue(categoryId, out var cat))
        {
            categorySlug = cat.Slug;
        }

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

public class MediaItem
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("source_url")] public string SourceUrl { get; set; } = "";
}

public class MediaCacheItem
{
    public int Id { get; init; }
    public string Url { get; init; } = "";
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
