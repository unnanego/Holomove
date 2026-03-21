using System.Net;
using AngleSharp.Html.Parser;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WordPressPCL.Models;

namespace Holomove;

public partial class WpMigrator
{
    private async Task FetchSourceData()
    {
        Console.WriteLine("\n  Fetching source data...");

        // Always refresh metadata (small)
        _sourceAuthors = (await _sourceWp.Users.GetAllAsync()).ToList();
        _sourceTags = (await _sourceWp.Tags.GetAllAsync()).ToList();
        _sourceCategories = (await _sourceWp.Categories.GetAllAsync()).ToList();
        Console.WriteLine($"  Source: {_sourceAuthors.Count} authors, {_sourceTags.Count} tags, {_sourceCategories.Count} categories");

        // Fetch all post IDs
        var allIds = await GetAllPostIds(Config.SourceWpApiUrl, useAuth: false);
        Console.WriteLine($"  Source: {allIds.Count} posts on API");

        // Only download posts we don't already have
        var missingIds = allIds.Where(id => !_sourcePosts.Any(p => p.Id == id)).ToList();

        if (missingIds.Count > 0)
        {
            Console.WriteLine($"  Downloading {missingIds.Count} new source posts...");
            var progress = new ProgressBar();
            for (var i = 0; i < missingIds.Count; i++)
            {
                progress.Update(i + 1, missingIds.Count, $"ID: {missingIds[i]}");
                try
                {
                    var post = await _sourceWp.Posts.GetByIDAsync(missingIds[i]);
                    _sourcePosts.Add(post);
                }
                catch { /* skip failed posts */ }
            }
            progress.Complete($"Downloaded {missingIds.Count} source post(s).");
        }

        Console.WriteLine($"  Total source posts: {_sourcePosts.Count}");
    }

    private async Task FetchTargetData()
    {
        Console.WriteLine("\n  Fetching target data...");

        try
        {
            _targetAuthors = (await _targetWp.Users.GetAllAsync()).ToList();
            _targetTags = (await _targetWp.Tags.GetAllAsync()).ToList();
            _targetCategories = (await _targetWp.Categories.GetAllAsync()).ToList();
        }
        catch
        {
            _targetAuthors = await FetchAllFromApi<User>("users") ?? [];
            _targetTags = await FetchAllFromApi<Tag>("tags") ?? [];
            _targetCategories = await FetchAllFromApi<Category>("categories") ?? [];
        }

        Console.WriteLine($"  Target: {_targetAuthors.Count} authors, {_targetTags.Count} tags, {_targetCategories.Count} categories");

        // Fetch all target posts
        _targetPosts.Clear();
        var page = 1;
        while (true)
        {
            try
            {
                var url = $"{Config.TargetWpApiUrl}wp/v2/posts?per_page=100&page={page}";
                var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode) break;

                var json = await response.Content.ReadAsStringAsync();
                var posts = JsonConvert.DeserializeObject<List<Post>>(json) ?? [];
                if (posts.Count == 0) break;

                _targetPosts.AddRange(posts);
                var width = Math.Max(Console.WindowWidth, 80);
                Console.Write($"\r  Fetching target posts: {_targetPosts.Count} (page {page})".PadRight(width - 1));
                page++;
            }
            catch { break; }
        }

        Console.WriteLine($"\n  Total target posts: {_targetPosts.Count}");
    }

    private async Task SyncTaxonomy()
    {
        Console.WriteLine("\n  Syncing taxonomy...");
        var created = 0;

        // Create missing tags
        foreach (var sourceTag in _sourceTags.Where(sourceTag => !_targetTagsDict.ContainsKey(sourceTag.Slug)))
        {
            try
            {
                var newTag = await _targetWp.Tags.CreateAsync(new Tag
                {
                    Name = sourceTag.Name, Slug = sourceTag.Slug, Description = sourceTag.Description
                });
                _targetTags.Add(newTag);
                _targetTagsDict[newTag.Slug] = newTag;
                created++;
            }
            catch
            {
                var existing = (await _targetWp.Tags.GetAllAsync(useAuth: true))
                    .FirstOrDefault(t => t.Slug == sourceTag.Slug || t.Name == sourceTag.Name);
                if (existing != null) _targetTagsDict[existing.Slug] = existing;
            }
        }

        // Create missing categories
        foreach (var sourceCat in _sourceCategories.Where(sourceCat => !_targetCategoriesDict.ContainsKey(sourceCat.Slug)))
        {
            try
            {
                var newCat = await _targetWp.Categories.CreateAsync(new Category
                {
                    Name = sourceCat.Name, Slug = sourceCat.Slug, Description = sourceCat.Description
                });
                _targetCategories.Add(newCat);
                _targetCategoriesDict[newCat.Slug] = newCat;
                created++;
            }
            catch
            {
                var existing = (await _targetWp.Categories.GetAllAsync(useAuth: true)).FirstOrDefault(c => c.Slug == sourceCat.Slug || c.Name == sourceCat.Name);
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

    private async Task CreatePostOnTarget(Post sourcePost)
    {
        // Resolve tags
        var tagIds = sourcePost.Tags
            .Where(id => _sourceTagsDict.ContainsKey(id))
            .Select(id => _sourceTagsDict[id].Slug)
            .Where(slug => _targetTagsDict.ContainsKey(slug))
            .Select(slug => _targetTagsDict[slug].Id)
            .ToList();

        // Resolve categories
        var categoryIds = sourcePost.Categories
            .Where(id => _sourceCategoriesDict.ContainsKey(id))
            .Select(id => _sourceCategoriesDict[id].Slug)
            .Where(slug => _targetCategoriesDict.ContainsKey(slug))
            .Select(slug => _targetCategoriesDict[slug].Id)
            .ToList();

        // Process content (shortcodes + HTML cleanup, no URL rewriting)
        var content = sourcePost.Content.Rendered;
        content = ProcessShortcodes(content);
        content = CleanupHtml(content);

        // Upload media to target (so files exist there)
        var uploadedMediaIds = await UploadAllPostMedia(sourcePost.Content.Rendered);

        // Resolve author
        var authorId = ResolveTargetAuthorId(sourcePost.Author);

        // Upload featured image
        int? featuredMediaId = null;
        if (sourcePost.FeaturedMedia > 0)
        {
            var featuredUrl = await GetMediaUrl(sourcePost.FeaturedMedia.Value);
            if (featuredUrl != null)
            {
                var uploaded = await UploadMedia(featuredUrl);
                if (uploaded != null) featuredMediaId = uploaded.Id;
            }
        }

        // Prepare excerpt
        var excerpt = sourcePost.Excerpt.Rendered;
        if (!string.IsNullOrEmpty(excerpt))
        {
            var parser = new HtmlParser();
            var doc = parser.ParseDocument($"<body>{excerpt}</body>");
            excerpt = WebUtility.HtmlDecode(doc.Body?.TextContent.Trim() ?? "");
        }

        // Create post
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

        // Attach media
        if (featuredMediaId.HasValue) uploadedMediaIds.Add(featuredMediaId.Value);
        foreach (var mediaId in uploadedMediaIds.Distinct()) await AttachMediaToPost(mediaId, createdPost.Id);

        // Update meta
        var allMeta = await GetPostMeta(sourcePost.Id);
        var customMeta = allMeta?.Where(kv => kv.Key.StartsWith("post_view")).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (customMeta is { Count: > 0 }) await UpdatePostMeta(createdPost.Id, customMeta);

        _targetPosts.Add(createdPost);
    }

    private async Task<bool> UpdatePostIfNeeded(Post sourcePost, Post targetPost)
    {
        var updates = new Dictionary<string, object>();

        // Compare author
        var expectedAuthorId = ResolveTargetAuthorId(sourcePost.Author);
        if (targetPost.Author != expectedAuthorId)
            updates["author"] = expectedAuthorId;

        // Compare tags
        var expectedTagIds = sourcePost.Tags
            .Where(id => _sourceTagsDict.ContainsKey(id))
            .Select(id => _sourceTagsDict[id].Slug)
            .Where(slug => _targetTagsDict.ContainsKey(slug))
            .Select(slug => _targetTagsDict[slug].Id)
            .OrderBy(x => x).ToList();

        if (!targetPost.Tags.OrderBy(x => x).SequenceEqual(expectedTagIds)) updates["tags"] = expectedTagIds;

        // Compare categories
        var expectedCatIds = sourcePost.Categories
            .Where(id => _sourceCategoriesDict.ContainsKey(id))
            .Select(id => _sourceCategoriesDict[id].Slug)
            .Where(slug => _targetCategoriesDict.ContainsKey(slug))
            .Select(slug => _targetCategoriesDict[slug].Id)
            .OrderBy(x => x).ToList();

        if (!targetPost.Categories.OrderBy(x => x).SequenceEqual(expectedCatIds)) updates["categories"] = expectedCatIds;

        // Compare featured media — upload if missing
        if (sourcePost.FeaturedMedia > 0 && (targetPost.FeaturedMedia is null or 0))
        {
            var featuredUrl = await GetMediaUrl(sourcePost.FeaturedMedia.Value);
            if (featuredUrl != null)
            {
                var uploaded = await UploadMedia(featuredUrl);
                if (uploaded != null)
                    updates["featured_media"] = uploaded.Id;
            }
        }

        // Upload content media to target (ensure files exist)
        await UploadAllPostMedia(sourcePost.Content.Rendered);

        if (updates.Count == 0) return false;

        // Send single update
        var response = await PostJsonAsync($"{Config.TargetWpApiUrl}wp/v2/posts/{targetPost.Id}", updates);
        return response.IsSuccessStatusCode;
    }

    private int ResolveTargetAuthorId(int sourceAuthorId)
    {
        if (!_sourceUsersDict.TryGetValue(sourceAuthorId, out var sourceAuthor))
            return 1; // admin fallback

        var targetAuthor = _targetAuthors.FirstOrDefault(a =>
            a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
            a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));

        return targetAuthor?.Id ?? 1;
    }

    private async Task FindAndDeleteDuplicates()
    {
        Console.WriteLine("\n  Checking for duplicates...");

        var targetPostsBySlug = _targetPosts.ToDictionary(p => p.Slug, p => p);
        var duplicates = new List<Post>();

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
                var url = $"{Config.TargetWpApiUrl}wp/v2/posts/{post.Id}?force=true";
                var request = CreateAuthenticatedRequest(HttpMethod.Delete, url);
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode) _targetPosts.RemoveAll(p => p.Id == post.Id);
            }
            catch { /* skip */ }
        }

        progress.Complete($"Deleted {duplicates.Count} duplicate(s).");
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
                var ids = JsonConvert.DeserializeObject<List<IdOnly>>(json) ?? [];
                if (ids.Count == 0) break;

                foreach (var id in ids) allIds.Add(id.Id);
                page++;
            }
            catch { break; }
        }

        return allIds;
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
        catch { return null; }
    }

    private async Task UpdatePostMeta(int postId, Dictionary<string, object> meta)
    {
        try
        {
            await PostJsonAsync($"{Config.TargetWpApiUrl}wp/v2/posts/{postId}", new { meta });
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
            {
                Console.WriteLine("\n  Posts pending migration:");
                foreach (var post in missing.OrderBy(p => p.Date))
                    Console.WriteLine($"    - {post.Slug} ({post.Date:yyyy-MM-dd})");
                break;
            }
            case > 20:
            {
                Console.WriteLine($"\n  First 20 posts pending migration:");
                foreach (var post in missing.OrderBy(p => p.Date).Take(20))
                    Console.WriteLine($"    - {post.Slug} ({post.Date:yyyy-MM-dd})");
                break;
            }
        }

        return Task.CompletedTask;
    }
}
