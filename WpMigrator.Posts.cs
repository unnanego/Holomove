using System.Net;
using AngleSharp.Html.Parser;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WordPressPCL.Models;

namespace Holomove;

public partial class WpMigrator
{
    private async Task DownloadAllPostsAndCompare()
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("CACHE COMPARISON REPORT");
        Console.WriteLine(new string('=', 60));

        var cachedSourceIds = _sourcePosts.Select(p => p.Id).ToHashSet();
        var cachedTargetIds = _targetPosts.Select(p => p.Id).ToHashSet();

        Console.WriteLine("\nCached data:");
        Console.WriteLine($"  Source: {cachedSourceIds.Count} posts");
        Console.WriteLine($"  Target: {cachedTargetIds.Count} posts");

        Console.WriteLine("\nFetching post IDs from APIs...");
        var apiSourceIds = await GetAllPostIds(Config.SourceWpApiUrl, useAuth: false);
        var apiTargetIds = await GetAllPostIds(Config.TargetWpApiUrl, useAuth: true);

        Console.WriteLine("\nAPI data:");
        Console.WriteLine($"  Source: {apiSourceIds.Count} posts");
        Console.WriteLine($"  Target: {apiTargetIds.Count} posts");

        Console.WriteLine("\n" + new string('-', 60));
        Console.WriteLine("CACHE VS API COMPARISON");
        Console.WriteLine(new string('-', 60));

        var staleSourceIds = cachedSourceIds.Except(apiSourceIds).ToList();
        var missingSourceIds = apiSourceIds.Except(cachedSourceIds).ToList();
        var staleTargetIds = cachedTargetIds.Except(apiTargetIds).ToList();
        var missingTargetIds = apiTargetIds.Except(cachedTargetIds).ToList();

        Console.WriteLine("\nSource posts:");
        Console.WriteLine($"  Stale (in cache but deleted from server): {staleSourceIds.Count}");
        Console.WriteLine($"  Missing from cache (need to download): {missingSourceIds.Count}");

        Console.WriteLine("\nTarget posts:");
        Console.WriteLine($"  Stale (in cache but deleted from server): {staleTargetIds.Count}");
        Console.WriteLine($"  Missing from cache (need to download): {missingTargetIds.Count}");

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
        Console.WriteLine("\nComparison report saved to: cacheComparisonReport.json");

        // Cleanup stale posts
        if (staleSourceIds.Count > 0)
        {
            Console.WriteLine($"\nRemoving {staleSourceIds.Count} stale source post(s) from cache...");
            foreach (var id in staleSourceIds)
            {
                var post = _sourcePosts.FirstOrDefault(p => p.Id == id);
                Console.WriteLine($"  - ID {id}: {post?.Slug ?? "unknown"}");
            }

            _sourcePosts.RemoveAll(p => staleSourceIds.Contains(p.Id));
        }

        if (staleTargetIds.Count > 0)
        {
            Console.WriteLine($"\nRemoving {staleTargetIds.Count} stale target post(s) from cache...");
            foreach (var id in staleTargetIds)
            {
                var post = _targetPosts.FirstOrDefault(p => p.Id == id);
                Console.WriteLine($"  - ID {id}: {post?.Slug ?? "unknown"}");
            }

            _targetPosts.RemoveAll(p => staleTargetIds.Contains(p.Id));
        }

        // Download missing source posts
        if (missingSourceIds.Count > 0)
        {
            Console.WriteLine($"\n  Downloading {missingSourceIds.Count} missing source posts...");
            var srcProgress = new ProgressBar();
            var count = 0;
            foreach (var (id, index) in missingSourceIds.Select((id, i) => (id, i)))
            {
                srcProgress.Update(index + 1, missingSourceIds.Count, $"ID: {id}");
                try
                {
                    var post = await _sourceWp.Posts.GetByIDAsync(id);
                    _sourcePosts.Add(post);
                    count++;
                    if (count % 100 == 0) await SaveCache();
                }
                catch (Exception ex)
                {
                    srcProgress.Complete($"Failed: post {id}: {ex.Message}");
                }
            }
            srcProgress.Complete($"Downloaded {count} source post(s).");

            Console.WriteLine();
            await SaveCache();
        }

        // Download missing target posts
        Console.WriteLine($"\nTarget posts to download: {missingTargetIds.Count}");
        Console.WriteLine($"Target posts already in cache: {_targetPosts.Count}");

        if (missingTargetIds.Count > 0)
        {
            Console.WriteLine("Downloading missing target posts in batches...");
            var page = 1;

            while (true)
            {
                try
                {
                    var url = $"{Config.TargetWpApiUrl}wp/v2/posts?per_page=100&page={page}";
                    var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
                    var response = await _httpClient.SendAsync(request);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"\n  Error fetching page {page}: {response.StatusCode}");
                        Console.WriteLine($"  Response: {errorContent[..Math.Min(500, errorContent.Length)]}");
                        if (response.StatusCode == HttpStatusCode.BadRequest) break;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    var posts = JsonConvert.DeserializeObject<List<Post>>(json) ?? [];
                    if (posts.Count == 0)
                    {
                        Console.WriteLine($"  Page {page}: empty, stopping");
                        break;
                    }

                    foreach (var post in posts.Where(post => _targetPosts.All(p => p.Id != post.Id)))
                    {
                        _targetPosts.Add(post);
                    }

                    Console.WriteLine($"  Page {page}: {posts.Count} posts, total in cache: {_targetPosts.Count}");
                    page++;

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

        Console.WriteLine("\nCache state after downloads:");
        Console.WriteLine($"  Source posts: {_sourcePosts.Count}");
        Console.WriteLine($"  Target posts: {_targetPosts.Count}");

        if (_sourcePosts.Count == 0)
        {
            Console.WriteLine("\nERROR: No source posts loaded. Check source API connection.");
            Environment.Exit(1);
        }

        if (apiTargetIds.Count > 0 && _targetPosts.Count == 0)
        {
            Console.WriteLine($"\nERROR: API reports {apiTargetIds.Count} target posts but cache is empty.");
            Console.WriteLine("Target post download failed. Check authentication and try again.");
            Environment.Exit(1);
        }

        // Migration status
        Console.WriteLine("\n" + new string('-', 60));
        Console.WriteLine("MIGRATION STATUS");
        Console.WriteLine(new string('-', 60));

        var targetSlugsSet = _targetPosts.Select(p => p.Slug).ToHashSet();
        var targetBaseSlugsSet = _targetPosts.Select(p => GetBaseSlug(p.Slug)).ToHashSet();

        var postsNeedingMigration = _sourcePosts
            .Where(p => !targetSlugsSet.Contains(p.Slug) && !targetBaseSlugsSet.Contains(p.Slug))
            .ToList();

        var alreadyMigratedPosts = _sourcePosts
            .Where(p => targetSlugsSet.Contains(p.Slug) || targetBaseSlugsSet.Contains(p.Slug))
            .ToList();

        Console.WriteLine($"\nSource posts: {_sourcePosts.Count}");
        Console.WriteLine($"  Already migrated: {alreadyMigratedPosts.Count}");
        Console.WriteLine($"  Need migration: {postsNeedingMigration.Count}");

        switch (postsNeedingMigration.Count)
        {
            case > 0 and <= 20:
            {
                Console.WriteLine("\nPosts needing migration:");
                foreach (var post in postsNeedingMigration.OrderBy(p => p.Date))
                {
                    Console.WriteLine($"  - {post.Slug} ({post.Date:yyyy-MM-dd})");
                }

                break;
            }
            case > 20:
            {
                Console.WriteLine($"\n(Showing first 20 of {postsNeedingMigration.Count} posts needing migration)");
                foreach (var post in postsNeedingMigration.OrderBy(p => p.Date).Take(20))
                {
                    Console.WriteLine($"  - {post.Slug} ({post.Date:yyyy-MM-dd})");
                }

                break;
            }
        }

        var migrationStatus = new
        {
            GeneratedAt = DateTime.Now,
            TotalSourcePosts = _sourcePosts.Count,
            TotalTargetPosts = _targetPosts.Count,
            AlreadyMigrated = alreadyMigratedPosts.Select(p => new { p.Id, p.Slug, p.Date }).ToList(),
            NeedMigration = postsNeedingMigration.Select(p => new { p.Id, p.Slug, p.Date }).ToList()
        };
        await SaveToCache("migrationStatus.json", migrationStatus);
        Console.WriteLine("\nMigration status saved to: migrationStatus.json");

        // Refresh metadata
        if (_sourceTags.Count == 0 || _sourceCategories.Count == 0 || _sourceAuthors.Count == 0)
        {
            Console.WriteLine("\nDownloading source metadata...");
            _sourceAuthors = (await _sourceWp.Users.GetAllAsync()).ToList();
            _sourceTags = (await _sourceWp.Tags.GetAllAsync()).ToList();
            _sourceCategories = (await _sourceWp.Categories.GetAllAsync()).ToList();
        }

        if (_targetTags.Count == 0 || _targetCategories.Count == 0 || _targetAuthors.Count == 0)
        {
            Console.WriteLine("Downloading target metadata...");
            try
            {
                _targetAuthors = (await _targetWp.Users.GetAllAsync()).ToList();
                _targetTags = (await _targetWp.Tags.GetAllAsync()).ToList();
                _targetCategories = (await _targetWp.Categories.GetAllAsync()).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WordPressPCL failed: {ex.Message}");
                Console.WriteLine("  Falling back to direct API calls...");
                _targetAuthors = await FetchAllFromApi<User>("users") ?? [];
                _targetTags = await FetchAllFromApi<Tag>("tags") ?? [];
                _targetCategories = await FetchAllFromApi<Category>("categories") ?? [];
            }
        }

        BuildLookupDictionaries();

        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine($"SUMMARY: {_sourcePosts.Count} source posts, {_targetPosts.Count} target posts");
        Console.WriteLine($"         {postsNeedingMigration.Count} posts ready for migration");
        Console.WriteLine(new string('=', 60));
    }

    private async Task<HashSet<int>> GetAllPostIds(string apiUrl, bool useAuth = false)
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
                    response = await _httpClient.SendAsync(request);
                }
                else
                {
                    response = await _httpClient.GetAsync(url);
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

    private async Task FindAndDeleteDuplicates()
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("FINDING DUPLICATES ON TARGET");
        Console.WriteLine(new string('=', 60));

        Console.WriteLine($"\nSource posts: {_sourcePosts.Count}");
        Console.WriteLine($"Target posts in cache: {_targetPosts.Count}");

        var targetPostsBySlug = _targetPosts.ToDictionary(p => p.Slug, p => p);
        var duplicates = new List<Post>();

        foreach (var sourcePost in _sourcePosts)
        {
            for (var suffix = 2; suffix <= 19; suffix++)
            {
                var potentialDuplicateSlug = $"{sourcePost.Slug}-{suffix}";

                if (targetPostsBySlug.TryGetValue(potentialDuplicateSlug, out var targetPost) &&
                    targetPost.Date == sourcePost.Date)
                {
                    duplicates.Add(targetPost);
                    Console.WriteLine($"  {sourcePost.Slug} -> {targetPost.Slug} (Date: {sourcePost.Date:yyyy-MM-dd HH:mm:ss})");
                }
            }
        }

        if (duplicates.Count == 0)
        {
            Console.WriteLine("\nNo duplicates found.");
            return;
        }

        Console.WriteLine($"\nDeleting {duplicates.Count} duplicate(s)...");

        foreach (var post in duplicates)
        {
            try
            {
                Console.Write($"  Deleting post {post.Id} ({post.Slug})...");
                var success = await DeletePost(post.Id);
                if (success)
                {
                    Console.WriteLine(" Done");
                    _targetPosts.RemoveAll(p => p.Id == post.Id);
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

    private async Task<bool> DeletePost(int postId)
    {
        try
        {
            var url = $"{Config.TargetWpApiUrl}wp/v2/posts/{postId}?force=true";
            var request = CreateAuthenticatedRequest(HttpMethod.Delete, url);
            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<PostSlugInfo?> CheckPostExistsBySlug(string slug)
    {
        try
        {
            var url = $"{Config.TargetWpApiUrl}wp/v2/posts?slug={Uri.EscapeDataString(slug)}&_fields=id,slug";
            var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request);

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

    private async Task ProcessPosts()
    {
        Console.WriteLine("\nProcessing posts...");

        var targetSlugs = _targetPosts.Select(p => p.Slug).ToHashSet();

        Console.WriteLine($"  Source posts: {_sourcePosts.Count}");
        Console.WriteLine($"  Target slugs in cache: {targetSlugs.Count}");

        var postsToProcess = _sourcePosts.Where(p => !targetSlugs.Contains(p.Slug));

        var postsList = postsToProcess.ToList();

        if (postsList.Count == 0)
        {
            Console.WriteLine("No posts to process - all posts already migrated.");
            return;
        }

        Console.WriteLine($"  Migrating {postsList.Count} post(s)...");
        var progress = new ProgressBar();

        for (var i = 0; i < postsList.Count; i++)
        {
            var sourcePost = postsList[i];
            progress.Update(i + 1, postsList.Count, sourcePost.Slug);

            try
            {
                await MigratePost(sourcePost);
                await SaveCache();
            }
            catch (Exception ex)
            {
                progress.Complete($"ERROR on {sourcePost.Slug}: {ex.Message}");
            }
        }

        progress.Complete($"Migrated {postsList.Count} post(s).");
    }

    private async Task MigratePost(Post sourcePost)
    {
        // Check if post already exists on target
        var existingPost = await CheckPostExistsBySlug(sourcePost.Slug);
        if (existingPost != null)
        {
            Console.WriteLine($"  Post already exists on target (ID: {existingPost.Id}), skipping.");
            if (_targetPosts.Any(p => p.Id == existingPost.Id)) return;
            try
            {
                var fullPost = await _targetWp.Posts.GetByIDAsync(existingPost.Id);
                _targetPosts.Add(fullPost);
            }
            catch
            {
                // Ignore - post exists, but we couldn't fetch full details
            }

            return;
        }

        // Migrate tags
        var tagIds = new List<int>();
        foreach (var sourceTagId in sourcePost.Tags)
        {
            if (!_sourceTagsDict.TryGetValue(sourceTagId, out var sourceTag)) continue;

            if (_targetTagsDict.TryGetValue(sourceTag.Slug, out var targetTag))
            {
                tagIds.Add(targetTag.Id);
            }
            else
            {
                try
                {
                    Console.WriteLine($"  Creating tag: {sourceTag.Name}");
                    var newTag = await _targetWp.Tags.CreateAsync(new Tag
                    {
                        Name = sourceTag.Name,
                        Slug = sourceTag.Slug,
                        Description = sourceTag.Description
                    });
                    _targetTags.Add(newTag);
                    _targetTagsDict[newTag.Slug] = newTag;
                    tagIds.Add(newTag.Id);
                }
                catch
                {
                    var existing = (await _targetWp.Tags.GetAllAsync(useAuth: true))
                        .FirstOrDefault(t => t.Slug == sourceTag.Slug || t.Name == sourceTag.Name);
                    if (existing != null)
                    {
                        _targetTagsDict[existing.Slug] = existing;
                        tagIds.Add(existing.Id);
                    }
                }
            }
        }

        // Migrate categories
        var categoryIds = new List<int>();
        foreach (var sourceCatId in sourcePost.Categories)
        {
            if (!_sourceCategoriesDict.TryGetValue(sourceCatId, out var sourceCat)) continue;

            if (_targetCategoriesDict.TryGetValue(sourceCat.Slug, out var targetCat))
            {
                categoryIds.Add(targetCat.Id);
            }
            else
            {
                try
                {
                    Console.WriteLine($"  Creating category: {sourceCat.Name}");
                    var newCat = await _targetWp.Categories.CreateAsync(new Category
                    {
                        Name = sourceCat.Name,
                        Slug = sourceCat.Slug,
                        Description = sourceCat.Description
                    });
                    _targetCategories.Add(newCat);
                    _targetCategoriesDict[newCat.Slug] = newCat;
                    categoryIds.Add(newCat.Id);
                }
                catch
                {
                    var existing = (await _targetWp.Categories.GetAllAsync(useAuth: true))
                        .FirstOrDefault(c => c.Slug == sourceCat.Slug || c.Name == sourceCat.Name);
                    if (existing != null)
                    {
                        _targetCategoriesDict[existing.Slug] = existing;
                        categoryIds.Add(existing.Id);
                    }
                }
            }
        }

        // Process content - migrate media
        var uploadedMediaIds = new List<int>();
        var content = sourcePost.Content.Rendered;
        content = await ProcessContentMedia(content, uploadedMediaIds);
        content = ProcessShortcodes(content);
        content = CleanupHtml(content);

        // Map author
        var authorId = 1; // Default to admin
        if (_sourceUsersDict.TryGetValue(sourcePost.Author, out var sourceAuthor))
        {
            var targetAuthor = _targetAuthorsDict.Values.FirstOrDefault(a =>
                a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));

            if (targetAuthor != null)
            {
                authorId = targetAuthor.Id;
                Console.WriteLine($"  Author: {sourceAuthor.Name} -> {targetAuthor.Name}");
            }
            else
            {
                Console.Write($"  Creating missing author: {sourceAuthor.Name}...");
                var newAuthor = await CreateAuthorOnTarget(sourceAuthor);
                if (newAuthor != null)
                {
                    _targetAuthors.Add(newAuthor);
                    _targetAuthorsDict[newAuthor.Slug] = newAuthor;
                    authorId = newAuthor.Id;
                    Console.WriteLine($" ID: {newAuthor.Id}");
                }
                else
                {
                    Console.WriteLine($" Failed, using admin");
                }
            }
        }

        // Upload featured image
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
                Console.WriteLine($"  Source featured media not found, falling back to first content image...");
                var parser = new HtmlParser();
                var doc = parser.ParseDocument($"<body>{sourcePost.Content.Rendered}</body>");

                var firstMedia = doc.QuerySelector("img[src]") ?? doc.QuerySelector("video");
                if (firstMedia is { TagName: "IMG" })
                {
                    var src = firstMedia.GetAttribute("src") ?? "";
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

        // Get custom fields (meta)
        var allMeta = await GetPostMeta(sourcePost.Id);
        var customMeta = allMeta?
            .Where(kv => kv.Key.StartsWith("post_view"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // Prepare excerpt
        var excerpt = sourcePost.Excerpt.Rendered;
        if (!string.IsNullOrEmpty(excerpt))
        {
            var excerptParser = new HtmlParser();
            var excerptDoc = excerptParser.ParseDocument($"<body>{excerpt}</body>");
            excerpt = WebUtility.HtmlDecode(excerptDoc.Body?.TextContent.Trim() ?? "");
        }

        // Create post on target
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

        var createdPost = await _targetWp.Posts.CreateAsync(newPost);
        Console.WriteLine($"  Created post ID: {createdPost.Id}");

        // Attach all uploaded media to this post
        if (featuredMediaId.HasValue)
        {
            uploadedMediaIds.Add(featuredMediaId.Value);
        }

        if (uploadedMediaIds.Count > 0)
        {
            var uniqueMediaIds = uploadedMediaIds.Distinct().ToList();
            Console.WriteLine($"  Attaching {uniqueMediaIds.Count} media item(s) to post...");
            foreach (var mediaId in uniqueMediaIds)
            {
                await AttachMediaToPost(mediaId, createdPost.Id);
            }
        }

        // Update custom fields
        if (customMeta is { Count: > 0 })
        {
            Console.WriteLine($"  Custom fields: {string.Join(", ", customMeta.Keys)}");
            await UpdatePostMeta(createdPost.Id, customMeta);
            Console.WriteLine($"  Updated {customMeta.Count} custom field(s)");
        }

        _targetPosts.Add(createdPost);
    }

    private async Task<Dictionary<string, object>?> GetPostMeta(int postId)
    {
        try
        {
            var url = $"{Config.SourceWpApiUrl}wp/v2/posts/{postId}?_fields=meta";
            var response = await _httpClient.GetAsync(url);

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

    private async Task UpdatePostMeta(int postId, Dictionary<string, object> meta)
    {
        try
        {
            var response = await PostJsonAsync($"{Config.TargetWpApiUrl}wp/v2/posts/{postId}", new { meta });

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

    private async Task VerifyAndFixExistingPostsMedia()
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("VERIFYING AND FIXING MEDIA FOR EXISTING POSTS");
        Console.WriteLine(new string('=', 60));

        var targetPostsBySlug = _targetPosts.ToDictionary(p => p.Slug, p => p);
        var targetPostsByBaseSlug = _targetPosts
            .GroupBy(p => GetBaseSlug(p.Slug))
            .ToDictionary(g => g.Key, g => g.First());

        var postsToFix = new List<(Post sourcePost, Post targetPost)>();

        foreach (var sourcePost in _sourcePosts)
        {
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

        Console.WriteLine("Fetching target post data in bulk...");
        var targetPostIds = postsToFix.Select(p => p.targetPost.Id).ToList();
        var targetPostData = await BatchFetchTargetPostData(targetPostIds);
        Console.WriteLine($"  Fetched data for {targetPostData.Count} posts.");

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
        var progress = new ProgressBar();

        for (var i = 0; i < postsToFix.Count; i++)
        {
            var (sourcePost, targetPost) = postsToFix[i];
            progress.Update(i + 1, postsToFix.Count, sourcePost.Slug);

            if (!targetPostData.TryGetValue(targetPost.Id, out var postData)) continue;

            // Check featured image
            var featuredMediaMissing = postData.featuredMedia is null or 0 || !existingMediaIds.Contains(postData.featuredMedia.Value);
            if (featuredMediaMissing)
            {
                if (sourcePost.FeaturedMedia > 0)
                {
                    var featuredUrl = await GetMediaUrl(sourcePost.FeaturedMedia.Value);
                    if (featuredUrl != null)
                    {
                        var uploaded = await UploadMedia(featuredUrl, targetPost.Id);
                        if (uploaded != null)
                        {
                            await SetPostFeaturedImage(targetPost.Id, uploaded.Id);
                            fixedFeaturedImages++;
                        }
                    }
                    else
                    {
                        if (await TrySetFeaturedFromContent(sourcePost, targetPost)) fixedFeaturedImages++;
                    }
                }
                else
                {
                    if (await TrySetFeaturedFromContent(sourcePost, targetPost)) fixedFeaturedImages++;
                }

                await Task.Delay(1000);
            }

            // Check content media
            var targetContent = postData.content;
            if (string.IsNullOrEmpty(targetContent)) continue;

            var targetMediaUrls = ExtractMediaUrls(targetContent);
            var urlReplacements = new Dictionary<string, string>();

            var sourceMediaUrls = ExtractMediaUrls(sourcePost.Content.Rendered);
            var sourceUrlsByFilename = sourceMediaUrls
                .Where(IsSourceMedia)
                .ToDictionary(
                    u => Path.GetFileName(new Uri(u).LocalPath),
                    u => u,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var targetMediaUrl in targetMediaUrls)
            {
                if (!targetMediaUrl.Contains(Config.TargetWpUrl) && !targetMediaUrl.Contains("new.holographica.space"))
                    continue;

                var fileName = Path.GetFileName(new Uri(targetMediaUrl).LocalPath);
                var mediaExists = _targetMediaByUrl.ContainsKey(targetMediaUrl) || _targetMediaByFilename.ContainsKey(fileName);

                if (mediaExists) continue;

                if (sourceUrlsByFilename.TryGetValue(fileName, out var sourceUrl))
                {
                    var uploaded = await UploadMedia(sourceUrl, targetPost.Id);
                    if (uploaded != null)
                    {
                        fixedMediaAttachments++;
                        if (!string.IsNullOrEmpty(uploaded.SourceUrl) &&
                            !uploaded.SourceUrl.Equals(targetMediaUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            urlReplacements[targetMediaUrl] = uploaded.SourceUrl;
                        }
                    }

                    await Task.Delay(1000);
                }
            }

            if (urlReplacements.Count > 0)
            {
                await UpdatePostContentUrls(targetPost.Id, urlReplacements);
            }
        }

        progress.Complete($"Fixed {fixedFeaturedImages} featured image(s), {fixedMediaAttachments} media attachment(s).");
    }

    private async Task<Dictionary<int, (int? featuredMedia, string? content)>> BatchFetchTargetPostData(List<int> postIds)
    {
        var result = new Dictionary<int, (int? featuredMedia, string? content)>();

        foreach (var batch in postIds.Chunk(100))
        {
            try
            {
                var ids = string.Join(",", batch);
                var url = $"{Config.TargetWpApiUrl}wp/v2/posts?include={ids}&_fields=id,featured_media,content&per_page={batch.Length}";
                var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
                var response = await _httpClient.SendAsync(request);

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
                foreach (var id in batch)
                {
                    try
                    {
                        var url = $"{Config.TargetWpApiUrl}wp/v2/posts/{id}?_fields=id,featured_media,content";
                        var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
                        var response = await _httpClient.SendAsync(request);
                        if (!response.IsSuccessStatusCode) continue;

                        var json = await response.Content.ReadAsStringAsync();
                        var post = JObject.Parse(json);
                        var featuredMedia = post["featured_media"]?.Value<int?>();
                        var content = post["content"]?["rendered"]?.ToString();
                        result[id] = (featuredMedia, content);
                    }
                    catch
                    {
                        /* skip */
                    }
                }
            }
        }

        return result;
    }

    private async Task<HashSet<int>> BatchVerifyMediaExist(List<int> mediaIds)
    {
        var existing = new HashSet<int>();

        foreach (var batch in mediaIds.Distinct().Chunk(100))
        {
            try
            {
                var ids = string.Join(",", batch);
                var url = $"{Config.TargetWpApiUrl}wp/v2/media?include={ids}&_fields=id&per_page={batch.Length}";
                var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                var items = JArray.Parse(json);
                foreach (var item in items)
                {
                    existing.Add(item["id"]!.Value<int>());
                }
            }
            catch
            {
                /* skip batch */
            }
        }

        return existing;
    }

    private async Task<bool> TrySetFeaturedFromContent(Post sourcePost, Post targetPost)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument($"<body>{sourcePost.Content.Rendered}</body>");

        var firstImg = doc.QuerySelector("img[src]");
        if (firstImg != null)
        {
            var src = firstImg.GetAttribute("src") ?? "";
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

        var iframe = doc.QuerySelector("iframe[src]");
        if (iframe == null) return false;

        var iframeSrc = iframe.GetAttribute("src") ?? "";
        var videoUrl = ExtractVideoUrl(iframeSrc);
        if (videoUrl == null) return false;

        Console.WriteLine($"  No image, setting featured video: {videoUrl}");
        await SetPostFeaturedVideo(targetPost.Id, videoUrl);
        return true;
    }

    private async Task UpdatePostContentUrls(int postId, Dictionary<string, string> urlReplacements)
    {
        try
        {
            var getUrl = $"{Config.TargetWpApiUrl}wp/v2/posts/{postId}?_fields=content";
            var getRequest = CreateAuthenticatedRequest(HttpMethod.Get, getUrl);
            var getResponse = await _httpClient.SendAsync(getRequest);

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

            var updatedContent = content;
            foreach (var (oldUrl, newUrl) in urlReplacements)
            {
                updatedContent = updatedContent.Replace(oldUrl, newUrl);
                var oldPath = oldUrl.Replace("https://", "").Replace("http://", "");
                var newPath = newUrl.Replace("https://", "").Replace("http://", "");
                updatedContent = updatedContent.Replace(oldPath, newPath);
            }

            if (updatedContent == content)
            {
                Console.WriteLine($"  No URL changes needed in content");
                return;
            }

            var updateResponse = await PostJsonAsync($"{Config.TargetWpApiUrl}wp/v2/posts/{postId}", new { content = updatedContent });
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
}