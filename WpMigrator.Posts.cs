using System.Net;
using AngleSharp.Html.Parser;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Holomove;

public partial class WpMigrator
{
    private async Task FetchSourceData()
    {
        Console.WriteLine("\n  Fetching source data...");

        Console.Write("  Loading metadata...");
        var authorsTask = FetchAllPaginated<WpUser>(_config.SourceWpApiUrl, "users");
        var tagsTask = FetchAllPaginated<WpTag>(_config.SourceWpApiUrl, "tags");
        var categoriesTask = FetchAllPaginated<WpCategory>(_config.SourceWpApiUrl, "categories");
        await Task.WhenAll(authorsTask, tagsTask, categoriesTask);
        _sourceAuthors = authorsTask.Result;
        _sourceTags = tagsTask.Result;
        _sourceCategories = categoriesTask.Result;
        Console.WriteLine($"\r  Source: {_sourceAuthors.Count} authors, {_sourceTags.Count} tags, {_sourceCategories.Count} categories");

        Console.WriteLine($"  {_sourcePosts.Count} posts from backup, fetching updates...");
        await FetchPostsInBulk(_config.SourceWpApiUrl, _sourcePosts, useAuth: false,
            fields: "id,slug,date,modified,status,title,content,excerpt,author,featured_media,tags,categories,meta");
        Console.WriteLine($"  Source: {_sourcePosts.Count} posts loaded");

        await BuildSourceMediaIndex();
    }

    private async Task FetchTargetData()
    {
        Console.WriteLine("\n  Fetching target data...");

        Console.Write("  Loading metadata...");
        var authorsTask = FetchAllPaginated<WpUser>(_config.TargetWpApiUrl, "users", useAuth: true);
        var tagsTask = FetchAllPaginated<WpTag>(_config.TargetWpApiUrl, "tags", useAuth: true);
        var categoriesTask = FetchAllPaginated<WpCategory>(_config.TargetWpApiUrl, "categories", useAuth: true);
        await Task.WhenAll(authorsTask, tagsTask, categoriesTask);
        _targetAuthors = authorsTask.Result;
        _targetTags = tagsTask.Result;
        _targetCategories = categoriesTask.Result;
        Console.WriteLine($"\r  Target: {_targetAuthors.Count} authors, {_targetTags.Count} tags, {_targetCategories.Count} categories");

        Console.WriteLine($"  {_targetPosts.Count} posts in cache, fetching updates...");
        await FetchPostsInBulk(_config.TargetWpApiUrl, _targetPosts, useAuth: true,
            fields: "id,slug,date,status,author,featured_media,tags,categories");
        Console.WriteLine($"  Target: {_targetPosts.Count} posts loaded");
        await SaveTargetPostCache();
    }

    private async Task SyncTaxonomy()
    {
        Console.WriteLine("\n  Syncing taxonomy...");
        var created = 0;

        foreach (var sourceTag in _sourceTags.Where(st => !_targetTagsDict.ContainsKey(st.Slug)))
        {
            var newTag = await CreateOnTarget<WpTag>("tags", new
            {
                name = sourceTag.Name, slug = sourceTag.Slug, description = sourceTag.Description ?? ""
            });

            if (newTag != null)
            {
                _targetTags.Add(newTag);
                _targetTagsDict[newTag.Slug] = newTag;
                created++;
            }
            else
            {
                // May already exist — refresh and find it
                var all = await FetchAllPaginated<WpTag>(_config.TargetWpApiUrl, "tags", useAuth: true);
                var existing = all.FirstOrDefault(t => t.Slug == sourceTag.Slug || t.Name == sourceTag.Name);
                if (existing != null) _targetTagsDict[existing.Slug] = existing;
            }
        }

        foreach (var sourceCat in _sourceCategories.Where(sc => !_targetCategoriesDict.ContainsKey(sc.Slug)))
        {
            var newCat = await CreateOnTarget<WpCategory>("categories", new
            {
                name = sourceCat.Name, slug = sourceCat.Slug, description = sourceCat.Description ?? ""
            });

            if (newCat != null)
            {
                _targetCategories.Add(newCat);
                _targetCategoriesDict[newCat.Slug] = newCat;
                created++;
            }
            else
            {
                var all = await FetchAllPaginated<WpCategory>(_config.TargetWpApiUrl, "categories", useAuth: true);
                var existing = all.FirstOrDefault(c => c.Slug == sourceCat.Slug || c.Name == sourceCat.Name);
                if (existing != null) _targetCategoriesDict[existing.Slug] = existing;
            }
        }

        Console.WriteLine($"  Created {created} missing tag(s)/category/ies.");
    }

    /// <summary>
    /// Find target post for a source slug. Handles WP slug collisions where target
    /// got "{slug}-N" suffix. Dates are compared with 24h tolerance because source
    /// and target WP may store dates in different timezones (REST converts on save).
    /// </summary>
    private static WpPost? FindTargetForSource(WpPost source, Dictionary<string, WpPost> targetBySlug)
    {
        if (targetBySlug.TryGetValue(source.Slug, out var exact)) return exact;
        for (var n = 2; n <= 19; n++)
        {
            if (targetBySlug.TryGetValue($"{source.Slug}-{n}", out var suffixed) &&
                Math.Abs((suffixed.Date - source.Date).TotalHours) < 24)
                return suffixed;
        }
        return null;
    }

    private async Task SyncAllPosts()
    {
        Console.WriteLine("\n  Syncing posts...");

        var targetPostsBySlug = _targetPosts.ToDictionary(p => p.Slug, p => p);
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var verified = 0;
        var done = 0;
        var total = _sourcePosts.Count;
        var progress = new ProgressBar();
        var postLock = new object();

        await Parallel.ForEachAsync(_sourcePosts, new ParallelOptions { MaxDegreeOfParallelism = 5 },
            async (sourcePost, _) =>
            {
                try
                {
                    // Skip posts already verified as fully synced
                    if (_verifiedPosts.ContainsKey(sourcePost.Slug))
                    {
                        Interlocked.Increment(ref verified);
                    }
                    else if (FindTargetForSource(sourcePost, targetPostsBySlug) is { } targetPost)
                    {
                        if (await UpdatePostIfNeeded(sourcePost, targetPost))
                            Interlocked.Increment(ref updated);
                        else
                            Interlocked.Increment(ref skipped);

                        // Mark verified after any successful examine — next run skips.
                        // Content rewrite always reports "changed" vs raw source, so without this
                        // we'd re-push content for every post on every run.
                        _verifiedPosts[sourcePost.Slug] = true;
                    }
                    else
                    {
                        var newPost = await CreatePostOnTarget(sourcePost);
                        if (newPost != null)
                        {
                            lock (postLock) { _targetPosts.Add(newPost); }
                            _verifiedPosts[sourcePost.Slug] = true;
                        }
                        Interlocked.Increment(ref created);
                    }
                }
                catch (Exception ex)
                {
                    progress.Complete($"Error on {sourcePost.Slug}: {ex.Message}");
                }

                var count = Interlocked.Increment(ref done);
                progress.Update(count, total, sourcePost.Slug);
            });

        progress.Complete($"Created {created}, updated {updated}, skipped {skipped}, verified {verified} (cached).");
    }

    private async Task RepairAllPosts()
    {
        Console.WriteLine("\n  Repairing posts (content URLs + featured images)...");

        var targetPostsBySlug = _targetPosts.ToDictionary(p => p.Slug, p => p);
        var repaired = 0;
        var skipped = 0;
        var done = 0;
        var total = _sourcePosts.Count;
        var progress = new ProgressBar();

        await Parallel.ForEachAsync(_sourcePosts, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (sourcePost, _) =>
            {
                try
                {
                    if (FindTargetForSource(sourcePost, targetPostsBySlug) is not { } targetPost)
                    {
                        Interlocked.Increment(ref skipped);
                    }
                    else
                    {
                        var updates = new Dictionary<string, object>();

                        // Fix content: re-process source content with correct URL rewriting
                        if (!string.IsNullOrEmpty(sourcePost.Content.Rendered) &&
                            sourcePost.Content.Rendered.Contains(_config.SourceWpUrl))
                        {
                            // Upload any missing media first so URL mapping is populated
                            var missingMedia = FindMissingMedia(sourcePost.Slug);
                            if (missingMedia.Count > 0)
                                await UploadAllPostMedia(sourcePost.Content.Rendered, targetPost.Id);

                            var content = sourcePost.Content.Rendered;
                            content = ProcessShortcodes(content);
                            content = CleanupHtml(content);
                            var rewritten = RewriteContentUrls(content);
                            if (rewritten != content)
                                updates["content"] = rewritten;
                        }

                        // Fix featured image if missing
                        if (targetPost.FeaturedMedia == 0 &&
                            _backupFeaturedMediaUrls.TryGetValue(sourcePost.Slug, out var featuredUrl))
                        {
                            var existingMedia = FindMediaOnTarget(featuredUrl);
                            if (existingMedia != null)
                            {
                                updates["featured_media"] = existingMedia.Id;
                            }
                            else
                            {
                                var uploaded = await UploadMedia(featuredUrl, attachToPostId: targetPost.Id);
                                if (uploaded != null)
                                    updates["featured_media"] = uploaded.Id;
                            }
                        }

                        if (updates.Count > 0)
                        {
                            await PostJsonAsync(
                                $"{_config.TargetWpApiUrl}wp/v2/posts/{targetPost.Id}", updates);
                            Interlocked.Increment(ref repaired);
                        }
                        else
                        {
                            Interlocked.Increment(ref skipped);
                        }

                        // Mark verified after examine — next run skips unless user clears cache.
                        _verifiedPosts[sourcePost.Slug] = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n    Error repairing {sourcePost.Slug}: {ex.Message}");
                }

                var count = Interlocked.Increment(ref done);
                progress.Update(count, total, sourcePost.Slug);
            });

        progress.Complete($"Repaired {repaired}, skipped {skipped} (already correct).");
    }

    private async Task<WpPost?> CreatePostOnTarget(WpPost sourcePost)
    {
        var tagIds = sourcePost.Tags
            .Where(id => _sourceTagsDict.ContainsKey(id))
            .Select(id => _sourceTagsDict[id].Slug)
            .Where(slug => _targetTagsDict.ContainsKey(slug))
            .Select(slug => _targetTagsDict[slug].Id)
            .ToList();

        var categoryIds = sourcePost.Categories
            .Where(id => _sourceCategoriesDict.ContainsKey(id))
            .Select(id => _sourceCategoriesDict[id].Slug)
            .Where(slug => _targetCategoriesDict.ContainsKey(slug))
            .Select(slug => _targetCategoriesDict[slug].Id)
            .ToList();

        var content = sourcePost.Content.Rendered;
        content = ProcessShortcodes(content);
        content = CleanupHtml(content);

        var uploadedMediaIds = await UploadAllPostMedia(sourcePost.Content.Rendered);
        var authorId = ResolveTargetAuthorId(sourcePost.Author);

        // Rewrite source domain URLs to actual target media URLs
        content = RewriteContentUrls(content);

        int? featuredMediaId = null;
        if (_backupFeaturedMediaUrls.TryGetValue(sourcePost.Slug, out var featuredUrl))
        {
            var uploaded = await UploadMedia(featuredUrl);
            if (uploaded != null) featuredMediaId = uploaded.Id;
        }

        var excerpt = sourcePost.Excerpt.Rendered;
        if (!string.IsNullOrEmpty(excerpt))
        {
            var parser = new HtmlParser();
            var doc = parser.ParseDocument($"<body>{excerpt}</body>");
            excerpt = WebUtility.HtmlDecode(doc.Body?.TextContent.Trim() ?? "");
        }

        var createdPost = await CreateOnTarget<WpPost>("posts", new
        {
            title = WebUtility.HtmlDecode(sourcePost.Title.Rendered),
            slug = sourcePost.Slug,
            content,
            excerpt = excerpt ?? "",
            status = sourcePost.Status,
            date = sourcePost.Date,
            tags = tagIds,
            categories = categoryIds,
            author = authorId,
            featured_media = featuredMediaId ?? 0
        });

        if (createdPost == null) return null;

        // Attach media in parallel
        if (featuredMediaId.HasValue) uploadedMediaIds.Add(featuredMediaId.Value);
        await Parallel.ForEachAsync(uploadedMediaIds.Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (mediaId, _) => await AttachMediaToPost(mediaId, createdPost.Id));

        // Update meta — use inline meta from bulk fetch, avoid extra API call
        var customMeta = sourcePost.Meta?
            .Where(kv => kv.Key.StartsWith("post_view"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (customMeta is { Count: > 0 }) await UpdatePostMeta(createdPost.Id, customMeta);

        return createdPost;
    }

    private async Task<bool> UpdatePostIfNeeded(WpPost sourcePost, WpPost targetPost)
    {
        var updates = new Dictionary<string, object>();

        var expectedAuthorId = ResolveTargetAuthorId(sourcePost.Author);
        if (targetPost.Author != expectedAuthorId)
            updates["author"] = expectedAuthorId;

        var expectedTagIds = sourcePost.Tags
            .Where(id => _sourceTagsDict.ContainsKey(id))
            .Select(id => _sourceTagsDict[id].Slug)
            .Where(slug => _targetTagsDict.ContainsKey(slug))
            .Select(slug => _targetTagsDict[slug].Id)
            .OrderBy(x => x).ToList();

        if (!targetPost.Tags.OrderBy(x => x).SequenceEqual(expectedTagIds)) updates["tags"] = expectedTagIds;

        var expectedCatIds = sourcePost.Categories
            .Where(id => _sourceCategoriesDict.ContainsKey(id))
            .Select(id => _sourceCategoriesDict[id].Slug)
            .Where(slug => _targetCategoriesDict.ContainsKey(slug))
            .Select(slug => _targetCategoriesDict[slug].Id)
            .OrderBy(x => x).ToList();

        if (!targetPost.Categories.OrderBy(x => x).SequenceEqual(expectedCatIds)) updates["categories"] = expectedCatIds;

        // Featured image: fix only if actually missing (0).
        // Skip for video-of-the-day posts — most don't have images.
        var isVideoOfTheDay = expectedCatIds.Any(id =>
            _targetCategoriesDict.Values.Any(c => c.Id == id &&
                c.Slug.Equals("video-of-the-day", StringComparison.OrdinalIgnoreCase)));

        if (!isVideoOfTheDay && targetPost.FeaturedMedia == 0 &&
            _backupFeaturedMediaUrls.TryGetValue(sourcePost.Slug, out var featuredUrl))
        {
            var existingMedia = FindMediaOnTarget(featuredUrl);
            if (existingMedia != null)
            {
                updates["featured_media"] = existingMedia.Id;
            }
            else
            {
                var uploaded = await UploadMedia(featuredUrl, attachToPostId: targetPost.Id);
                if (uploaded != null)
                    updates["featured_media"] = uploaded.Id;
            }
        }

        // Check for missing inline media — upload first so URL mapping is populated
        var missingMedia = FindMissingMedia(sourcePost.Slug);
        if (missingMedia.Count > 0)
            await UploadAllPostMedia(sourcePost.Content.Rendered, targetPost.Id);

        // Rewrite content URLs from source domain to actual target media URLs
        if (sourcePost.Content.Rendered.Contains(_config.SourceWpUrl))
        {
            var content = sourcePost.Content.Rendered;
            content = ProcessShortcodes(content);
            content = CleanupHtml(content);
            var rewritten = RewriteContentUrls(content);
            // Only push the update if URLs actually changed
            if (rewritten != content)
                updates["content"] = rewritten;
        }

        if (updates.Count == 0 && missingMedia.Count == 0) return false;

        Console.WriteLine($"\n    Updating {sourcePost.Slug}: {string.Join(", ", updates.Keys)}{(missingMedia.Count > 0 ? $", {missingMedia.Count} missing media" : "")}");

        if (updates.Count > 0)
            await PostJsonAsync($"{_config.TargetWpApiUrl}wp/v2/posts/{targetPost.Id}", updates);
        return true;
    }

    private int ResolveTargetAuthorId(int sourceAuthorId)
    {
        if (!_sourceUsersDict.TryGetValue(sourceAuthorId, out var sourceAuthor))
            return 1;

        if (_targetAuthorsBySlug.TryGetValue(sourceAuthor.Slug, out var bySlug))
            return bySlug.Id;
        if (_targetAuthorsByName.TryGetValue(sourceAuthor.Name, out var byName))
            return byName.Id;
        return 1;
    }

    private async Task FindAndDeleteDuplicates()
    {
        Console.WriteLine("\n  Checking for duplicates...");

        var targetPostsBySlug = _targetPosts.ToDictionary(p => p.Slug, p => p);
        var duplicates = new List<WpPost>();

        foreach (var sourcePost in _sourcePosts)
        {
            // Only consider "-N" suffixes as duplicates if bare slug also exists on target —
            // otherwise the "-N" post is the canonical copy (slug collision on creation).
            if (!targetPostsBySlug.ContainsKey(sourcePost.Slug)) continue;

            for (var suffix = 2; suffix <= 19; suffix++)
            {
                if (targetPostsBySlug.TryGetValue($"{sourcePost.Slug}-{suffix}", out var targetPost) &&
                    targetPost.Date == sourcePost.Date)
                {
                    duplicates.Add(targetPost);
                }
            }
        }

        if (duplicates.Count == 0)
        {
            Console.WriteLine("  No duplicates found.");
            return;
        }

        Console.WriteLine($"  Deleting {duplicates.Count} duplicate(s)...");
        var progress = new ProgressBar();

        for (var i = 0; i < duplicates.Count; i++)
        {
            var post = duplicates[i];
            progress.Update(i + 1, duplicates.Count, post.Slug);
            try
            {
                var url = $"{_config.TargetWpApiUrl}wp/v2/posts/{post.Id}?force=true";
                var request = CreateAuthenticatedRequest(HttpMethod.Delete, url);
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode) _targetPosts.RemoveAll(p => p.Id == post.Id);
            }
            catch { /* skip */ }
        }

        progress.Complete($"Deleted {duplicates.Count} duplicate(s).");
    }

    private async Task FetchPostsInBulk(string apiUrl, List<WpPost> targetList, bool useAuth = false,
        string? fields = null)
    {
        var existingIds = targetList.Where(p => p.Id > 0).Select(p => p.Id).ToHashSet();
        // Build slug→index map for stub posts (Id==0) so we can replace them in O(1)
        var stubsBySlug = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var idx = 0; idx < targetList.Count; idx++)
        {
            if (targetList[idx].Id == 0)
                stubsBySlug.TryAdd(targetList[idx].Slug, idx);
        }

        var width = Math.Max(Console.WindowWidth, 80);
        var extraQuery = string.IsNullOrEmpty(fields) ? null : $"_fields={fields}";

        var (firstPage, totalPages) = await FetchPageAsync<WpPost>(apiUrl, "posts", 1, useAuth, extraQuery);

        var fetched = 0;
        var mergeLock = new object();

        void Merge(List<WpPost> posts)
        {
            lock (mergeLock)
            {
                foreach (var post in posts)
                {
                    if (existingIds.Contains(post.Id)) continue;
                    if (stubsBySlug.TryGetValue(post.Slug, out var stubIndex))
                    {
                        targetList[stubIndex] = post;
                        stubsBySlug.Remove(post.Slug);
                    }
                    else
                    {
                        targetList.Add(post);
                    }
                    existingIds.Add(post.Id);
                    fetched++;
                }
            }
        }

        Merge(firstPage);
        Console.Write($"\r  Downloading posts: page 1/{totalPages}".PadRight(width - 1));

        if (totalPages > 1)
        {
            var pagesDone = 1;
            await Parallel.ForEachAsync(Enumerable.Range(2, totalPages - 1),
                new ParallelOptions { MaxDegreeOfParallelism = 6 },
                async (page, _) =>
                {
                    var (posts, _) = await FetchPageAsync<WpPost>(apiUrl, "posts", page, useAuth, extraQuery);
                    Merge(posts);
                    var done = Interlocked.Increment(ref pagesDone);
                    Console.Write($"\r  Downloading posts: page {done}/{totalPages} ({fetched} new)".PadRight(width - 1));
                });
        }

        Console.WriteLine($"\r  Downloaded {fetched} posts in {totalPages} pages.".PadRight(width - 1));
    }

    private async Task<Dictionary<string, object>?> GetPostMeta(int postId)
    {
        try
        {
            var url = $"{_config.SourceWpApiUrl}wp/v2/posts/{postId}?_fields=meta";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var result = JObject.Parse(json);
            return result["meta"]?.ToObject<Dictionary<string, object>>();
        }
        catch { return null; }
    }

    private async Task UpdatePostMeta(int postId, Dictionary<string, object> meta)
    {
        try
        {
            await PostJsonAsync($"{_config.TargetWpApiUrl}wp/v2/posts/{postId}", new { meta });
        }
        catch { /* best effort */ }
    }

    private Task PrintStatus()
    {
        var targetSlugs = _targetPosts.Select(p => p.Slug).ToHashSet();
        var missing = _sourcePosts.Where(p => !targetSlugs.Contains(p.Slug)).ToList();
        var migrated = _sourcePosts.Count - missing.Count;

        Console.WriteLine($"\n  Source: {_sourcePosts.Count} posts, {_sourceTags.Count} tags, {_sourceCategories.Count} categories, {_sourceAuthors.Count} authors");
        Console.WriteLine($"  Target: {_targetPosts.Count} posts, {_targetTags.Count} tags, {_targetCategories.Count} categories, {_targetAuthors.Count} authors");
        Console.WriteLine($"  Migrated: {migrated}, Pending: {missing.Count}");

        switch (missing.Count)
        {
            case > 0 and <= 20:
                Console.WriteLine("\n  Posts pending migration:");
                foreach (var post in missing.OrderBy(p => p.Date))
                    Console.WriteLine($"    - {post.Slug} ({post.Date:yyyy-MM-dd})");
                break;
            case > 20:
                Console.WriteLine($"\n  First 20 posts pending migration:");
                foreach (var post in missing.OrderBy(p => p.Date).Take(20))
                    Console.WriteLine($"    - {post.Slug} ({post.Date:yyyy-MM-dd})");
                break;
        }

        return Task.CompletedTask;
    }
}
