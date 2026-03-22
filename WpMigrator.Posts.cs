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
        _sourceAuthors = await FetchAllPaginated<WpUser>(_config.SourceWpApiUrl, "users");
        _sourceTags = await FetchAllPaginated<WpTag>(_config.SourceWpApiUrl, "tags");
        _sourceCategories = await FetchAllPaginated<WpCategory>(_config.SourceWpApiUrl, "categories");
        Console.WriteLine($"\r  Source: {_sourceAuthors.Count} authors, {_sourceTags.Count} tags, {_sourceCategories.Count} categories");

        Console.WriteLine($"  {_sourcePosts.Count} posts in cache, fetching updates...");
        await FetchPostsInBulk(_config.SourceWpApiUrl, _sourcePosts, useAuth: false);
        Console.WriteLine($"  Source: {_sourcePosts.Count} posts loaded");
    }

    private async Task FetchTargetData()
    {
        Console.WriteLine("\n  Fetching target data...");

        Console.Write("  Loading metadata...");
        _targetAuthors = await FetchAllPaginated<WpUser>(_config.TargetWpApiUrl, "users", useAuth: true);
        _targetTags = await FetchAllPaginated<WpTag>(_config.TargetWpApiUrl, "tags", useAuth: true);
        _targetCategories = await FetchAllPaginated<WpCategory>(_config.TargetWpApiUrl, "categories", useAuth: true);
        Console.WriteLine($"\r  Target: {_targetAuthors.Count} authors, {_targetTags.Count} tags, {_targetCategories.Count} categories");

        Console.WriteLine($"  {_targetPosts.Count} posts in cache, fetching updates...");
        await FetchPostsInBulk(_config.TargetWpApiUrl, _targetPosts, useAuth: true);
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

    private async Task SyncAllPosts()
    {
        Console.WriteLine("\n  Syncing posts...");

        var targetPostsBySlug = _targetPosts.ToDictionary(p => p.Slug, p => p);
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var progress = new ProgressBar();

        for (var i = 0; i < _sourcePosts.Count; i++)
        {
            var sourcePost = _sourcePosts[i];
            progress.Update(i + 1, _sourcePosts.Count, sourcePost.Slug);

            try
            {
                if (targetPostsBySlug.TryGetValue(sourcePost.Slug, out var targetPost))
                {
                    if (await UpdatePostIfNeeded(sourcePost, targetPost))
                        updated++;
                    else
                        skipped++;
                }
                else
                {
                    await CreatePostOnTarget(sourcePost);
                    created++;
                }
            }
            catch (Exception ex)
            {
                progress.Complete($"Error on {sourcePost.Slug}: {ex.Message}");
            }
        }

        progress.Complete($"Created {created}, updated {updated}, skipped {skipped} (already in sync).");
    }

    private async Task CreatePostOnTarget(WpPost sourcePost)
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

        int? featuredMediaId = null;
        if (sourcePost.FeaturedMedia > 0)
        {
            var featuredUrl = await GetMediaUrl(sourcePost.FeaturedMedia);
            if (featuredUrl != null)
            {
                var uploaded = await UploadMedia(featuredUrl);
                if (uploaded != null) featuredMediaId = uploaded.Id;
            }
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

        if (createdPost == null) return;

        // Attach media
        if (featuredMediaId.HasValue) uploadedMediaIds.Add(featuredMediaId.Value);
        foreach (var mediaId in uploadedMediaIds.Distinct()) await AttachMediaToPost(mediaId, createdPost.Id);

        // Update meta
        var allMeta = await GetPostMeta(sourcePost.Id);
        var customMeta = allMeta?.Where(kv => kv.Key.StartsWith("post_view")).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (customMeta is { Count: > 0 }) await UpdatePostMeta(createdPost.Id, customMeta);

        _targetPosts.Add(createdPost);
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

        if (sourcePost.FeaturedMedia > 0 && targetPost.FeaturedMedia == 0)
        {
            var featuredUrl = await GetMediaUrl(sourcePost.FeaturedMedia);
            if (featuredUrl != null)
            {
                var uploaded = await UploadMedia(featuredUrl);
                if (uploaded != null)
                    updates["featured_media"] = uploaded.Id;
            }
        }

        await UploadAllPostMedia(sourcePost.Content.Rendered);

        if (updates.Count == 0) return false;

        var response = await PostJsonAsync($"{_config.TargetWpApiUrl}wp/v2/posts/{targetPost.Id}", updates);
        return response.IsSuccessStatusCode;
    }

    private int ResolveTargetAuthorId(int sourceAuthorId)
    {
        if (!_sourceUsersDict.TryGetValue(sourceAuthorId, out var sourceAuthor))
            return 1;

        var targetAuthor = _targetAuthors.FirstOrDefault(a =>
            a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
            a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));

        return targetAuthor?.Id ?? 1;
    }

    private async Task FindAndDeleteDuplicates()
    {
        Console.WriteLine("\n  Checking for duplicates...");

        var targetPostsBySlug = _targetPosts.ToDictionary(p => p.Slug, p => p);
        var duplicates = new List<WpPost>();

        foreach (var sourcePost in _sourcePosts)
        {
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

    private async Task FetchPostsInBulk(string apiUrl, List<WpPost> targetList, bool useAuth = false)
    {
        var existingIds = targetList.Where(p => p.Id > 0).Select(p => p.Id).ToHashSet();
        var page = 1;
        var width = Math.Max(Console.WindowWidth, 80);
        var fetched = 0;

        while (true)
        {
            try
            {
                var url = $"{apiUrl}wp/v2/posts?per_page=100&page={page}";

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

                if (!response.IsSuccessStatusCode) break;

                var json = await response.Content.ReadAsStringAsync();
                var posts = JsonConvert.DeserializeObject<List<WpPost>>(json) ?? [];
                if (posts.Count == 0) break;

                foreach (var post in posts)
                {
                    if (existingIds.Contains(post.Id)) continue;

                    var stubIndex = targetList.FindIndex(p => p.Slug == post.Slug && p.Id == 0);
                    if (stubIndex >= 0)
                        targetList[stubIndex] = post;
                    else
                        targetList.Add(post);

                    existingIds.Add(post.Id);
                    fetched++;
                }

                Console.Write($"\r  Downloading posts: {fetched} new (page {page})".PadRight(width - 1));
                page++;
            }
            catch { break; }
        }

        Console.WriteLine($"\r  Downloaded {fetched} posts in {page - 1} pages.".PadRight(width - 1));
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
