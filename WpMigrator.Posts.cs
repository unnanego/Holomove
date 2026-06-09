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
        // Dropped `meta` from bulk fetch — custom-fields-api.php returns ALL post_meta
        // per item via get_post_meta(), which is heavy on a large site. Meta is only
        // needed when CREATING a new target post; lazy-fetch there via GetPostMeta().
        await FetchPostsInBulk(_config.SourceWpApiUrl, _sourcePosts, useAuth: false,
            fields: "id,slug,link,date,modified,status,title,content,excerpt,author,featured_media,tags,categories");
        Console.WriteLine($"  Source: {_sourcePosts.Count} posts loaded");

        RemoveOrphanBackupPosts();

        await BuildSourceMediaIndex();
    }

    /// <summary>
    /// After live merge, any _sourcePosts entry still at Id==0 is a stub from backup
    /// with no live counterpart — slug renamed or deleted upstream. Remove from
    /// in-memory list and delete its backup folder so it doesn't get re-synced as
    /// a ghost post on target.
    /// </summary>
    private void RemoveOrphanBackupPosts()
    {
        var orphans = _sourcePosts.Where(p => p.Id == 0).ToList();
        if (orphans.Count == 0) return;

        Console.WriteLine($"  Cleaning {orphans.Count} orphan backup post(s) (slug renamed or deleted upstream):");
        foreach (var orphan in orphans)
        {
            var folder = GetPostBackupFolder(orphan);
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                    Console.WriteLine($"    removed: {orphan.Slug}");
                }
                else
                {
                    Console.WriteLine($"    skipped (no folder): {orphan.Slug}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    failed: {orphan.Slug} — {ex.Message}");
            }
            _sourcePosts.Remove(orphan);
            _backupFeaturedMediaUrls.TryRemove(orphan.Slug, out _);
            _backupMediaUrls.TryRemove(orphan.Slug, out _);
        }
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

    private async Task SyncAllPosts(Dictionary<string, string>? targetContentsBySlug = null)
    {
        Console.WriteLine("\n  Syncing posts...");

        var targetPostsBySlug = BuildBySlug(_targetPosts, p => p.Slug, "target posts");
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
                    // Defense-in-depth: stub posts (Id==0) shouldn't reach here after
                    // RemoveOrphanBackupPosts, but skip just in case to avoid ghost creation.
                    if (sourcePost.Id == 0)
                    {
                        Interlocked.Increment(ref skipped);
                    }
                    else
                    {
                        var targetPost = FindTargetForSource(sourcePost, targetPostsBySlug);

                        string? targetContent = null;
                        if (targetPost != null && targetContentsBySlug != null &&
                            targetContentsBySlug.TryGetValue(targetPost.Slug, out var tc))
                            targetContent = tc;

                        // A previous buggy run may have marked posts verified while skipping
                        // their featured image (empty FeaturedMediaUrl in backup file, no
                        // fallback resolution). Re-examine if source has a featured image
                        // but target doesn't — UpdatePostIfNeeded will set it via ResolveFeaturedUrl.
                        var needsFeaturedRecheck = targetPost != null
                                                   && sourcePost.FeaturedMedia > 0
                                                   && targetPost.FeaturedMedia == 0;

                        // Same rationale for body images: pre-fullyResolved-fix runs marked
                        // posts verified even when target content still had source URLs.
                        // If target body still references the source domain, force a recheck
                        // so UpdatePostIfNeeded can upload missing media and rewrite the URLs.
                        // Only recheck for *fixable* URLs — bodies whose remaining source URLs
                        // are all permanent link rot (known-dead) can never resolve, so keep
                        // them in the verified-skip fast path instead of re-examining forever.
                        var needsContentRecheck = !string.IsNullOrEmpty(targetContent) &&
                                                  HasFixableSourceMedia(targetContent);

                        if (_verifiedPosts.ContainsKey(sourcePost.Slug) &&
                            !needsFeaturedRecheck && !needsContentRecheck)
                        {
                            Interlocked.Increment(ref verified);
                        }
                        else if (targetPost != null)
                        {
                            var (changed, fullyResolved) = await UpdatePostIfNeeded(sourcePost, targetPost, targetContent);
                            if (changed)
                                Interlocked.Increment(ref updated);
                            else
                                Interlocked.Increment(ref skipped);

                            // Mark verified only when content has no unresolved source URLs.
                            // Otherwise the post will be re-examined next run, giving a chance
                            // to retry uploads / rewrites once media becomes available.
                            if (fullyResolved)
                                _verifiedPosts[sourcePost.Slug] = true;
                        }
                        else
                        {
                            var newPost = await CreatePostOnTarget(sourcePost);
                            if (newPost != null)
                            {
                                lock (postLock) { _targetPosts.Add(newPost); }

                                // Empirically, fresh posts from CreatePostOnTarget often come
                                // back with featured_media=0 and source-domain URLs in body —
                                // the exact same upload+rewrite logic that succeeds on the
                                // update path. Symptom users see: "first migrate skips the
                                // new post's images, second migrate fixes them." Run the
                                // update path immediately against the just-created post so
                                // a single migrate run is enough.
                                var needsRepair = HasUnresolvedSourceMedia(newPost.Content.Rendered)
                                                  || (sourcePost.FeaturedMedia > 0 && newPost.FeaturedMedia == 0);
                                var resolved = !HasUnresolvedSourceMedia(newPost.Content.Rendered);
                                if (needsRepair)
                                {
                                    var (_, fullyResolved) = await UpdatePostIfNeeded(
                                        sourcePost, newPost, newPost.Content.Rendered);
                                    resolved = fullyResolved;
                                }

                                if (resolved)
                                    _verifiedPosts[sourcePost.Slug] = true;
                            }
                            Interlocked.Increment(ref created);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n    Error on {sourcePost.Slug}: {ex.Message}");
                }

                var count = Interlocked.Increment(ref done);
                progress.Update(count, total, sourcePost.Slug);

                // Flush verified + dead-media caches periodically so a mid-run crash/kill
                // preserves progress. Dead-media (the link-rot live-fetch results) is otherwise
                // only saved at run end / Ctrl-C; a hard kill would re-probe every dead URL next
                // run. Both files are small; target-media-cache is left to end/cancel (it's 30MB+
                // and on a Google-Drive-synced path, and uploaded media is re-discoverable via
                // DownloadTargetMediaList anyway).
                if (count % 100 == 0)
                {
                    try { SaveVerifiedPostsCache(); SaveDeadSourceMediaCache(); } catch { /* best effort */ }
                }
            });

        SaveVerifiedPostsCache();
        progress.Complete($"Created {created}, updated {updated}, skipped {skipped}, verified {verified} (cached).");
    }

    private async Task RepairAllPosts()
    {
        Console.WriteLine("\n  Repairing posts (content URLs + featured images)...");

        var targetPostsBySlug = BuildBySlug(_targetPosts, p => p.Slug, "target posts");
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
                                await UploadAllPostMedia(sourcePost.Content.Rendered, targetPost.Id, sourcePost.Link);

                            var content = sourcePost.Content.Rendered;
                            content = ProcessShortcodes(content);
                            content = CleanupHtml(content);
                            var rewritten = RewriteContentUrls(content);
                            if (rewritten != content)
                                updates["content"] = rewritten;
                        }

                        // Fix featured image if missing — resolve via backup → in-memory index → live API
                        if (targetPost.FeaturedMedia == 0)
                        {
                            var featuredUrl = await ResolveFeaturedUrl(sourcePost);
                            if (!string.IsNullOrEmpty(featuredUrl))
                            {
                                var existingMedia = FindMediaOnTarget(featuredUrl);
                                if (existingMedia != null)
                                {
                                    updates["featured_media"] = existingMedia.Id;
                                }
                                else
                                {
                                    var uploaded = await UploadMedia(featuredUrl, attachToPostId: targetPost.Id, postLink: sourcePost.Link);
                                    if (uploaded != null)
                                        updates["featured_media"] = uploaded.Id;
                                }
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

        // Upload from the same post-processed content that we'll rewrite and send.
        // Using the raw source content misses URLs that ProcessShortcodes introduces
        // (e.g. [video src=…] → <video src=…>), leaving them as source-domain in the
        // final post body because RewriteContentUrls can't find them in the target lib.
        var uploadedMediaIds = await UploadAllPostMedia(content, postLink: sourcePost.Link);
        var authorId = ResolveTargetAuthorId(sourcePost.Author);

        // Rewrite source domain URLs to actual target media URLs
        content = RewriteContentUrls(content);

        int? featuredMediaId = null;
        // Resolve featured URL with fallbacks: backup-file dict → in-memory source-media
        // index → direct source API. Backup files written before BuildSourceMediaIndex
        // succeeded may have empty FeaturedMediaUrl, leaving the dict missing entries.
        var featuredUrl = await ResolveFeaturedUrl(sourcePost);
        if (!string.IsNullOrEmpty(featuredUrl))
        {
            var uploaded = await UploadMedia(featuredUrl, postLink: sourcePost.Link);
            if (uploaded != null) featuredMediaId = uploaded.Id;
            else Console.WriteLine($"\n    {sourcePost.Slug}: featured upload failed for {featuredUrl}");
        }
        else if (sourcePost.FeaturedMedia > 0)
        {
            Console.WriteLine($"\n    {sourcePost.Slug}: source has featured_media={sourcePost.FeaturedMedia} but URL not resolved");
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

        // Lazy-fetch meta only at create time (bulk fetch drops `meta` for perf).
        // Only post_view* keys are propagated to target.
        var sourceMeta = sourcePost.Meta ?? await GetPostMeta(sourcePost.Id);
        var customMeta = sourceMeta?
            .Where(kv => kv.Key.StartsWith("post_view"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (customMeta is { Count: > 0 }) await UpdatePostMeta(createdPost.Id, customMeta);

        return createdPost;
    }

    private async Task<(bool Changed, bool FullyResolved)> UpdatePostIfNeeded(
        WpPost sourcePost, WpPost targetPost, string? targetContent = null)
    {
        var updates = new Dictionary<string, object>();
        var fullyResolved = true;

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

        if (!isVideoOfTheDay && targetPost.FeaturedMedia == 0)
        {
            var featuredUrl = await ResolveFeaturedUrl(sourcePost);
            if (!string.IsNullOrEmpty(featuredUrl))
            {
                var existingMedia = FindMediaOnTarget(featuredUrl);
                if (existingMedia != null)
                {
                    updates["featured_media"] = existingMedia.Id;
                }
                else
                {
                    var uploaded = await UploadMedia(featuredUrl, attachToPostId: targetPost.Id, postLink: sourcePost.Link);
                    if (uploaded != null)
                        updates["featured_media"] = uploaded.Id;
                }
            }
        }

        // Process once, then both upload and rewrite key off the same content —
        // otherwise shortcode-introduced URLs (e.g. [video src=…] → <video src=…>)
        // are missed by the raw-based upload and left source-domain after rewrite.
        var content = sourcePost.Content.Rendered;
        content = ProcessShortcodes(content);
        content = CleanupHtml(content);

        var missingMedia = FindMissingMedia(sourcePost.Slug);
        // Always upload from processed content. UploadMedia short-circuits when no
        // source URLs are present and uses the existing-media fast path otherwise,
        // so this stays cheap on posts that don't need any upload work.
        await UploadAllPostMedia(content, targetPost.Id, sourcePost.Link);

        // Rewrite content URLs from source domain to actual target media URLs
        if (HasSourceContentUrls(sourcePost.Content.Rendered))
        {
            var rewritten = RewriteContentUrls(content);

            // With actual target content, push only when target still has a *fixable*
            // source URL — avoids retriggering save_post hooks on already-clean posts AND
            // on link-rot posts whose only remaining source URLs are permanently dead
            // (re-pushing the same body every run was the main reason migrate took hours).
            // Without target content, fall back to comparing against the post-processed source.
            var shouldPush = targetContent == null
                ? rewritten != content
                : HasFixableSourceMedia(targetContent);

            if (shouldPush)
                updates["content"] = rewritten;
            // Block the verified-cache only if a still-fixable URL remains. Known-dead
            // URLs can never resolve, so don't keep the post out of the cache for them.
            if (HasFixableSourceMedia(rewritten))
                fullyResolved = false;
        }

        if (updates.Count == 0 && missingMedia.Count == 0) return (false, fullyResolved);

        Console.WriteLine($"\n    Updating {sourcePost.Slug}: {string.Join(", ", updates.Keys)}{(missingMedia.Count > 0 ? $", {missingMedia.Count} missing media" : "")}");

        if (updates.Count > 0)
            await PostJsonAsync($"{_config.TargetWpApiUrl}wp/v2/posts/{targetPost.Id}", updates);
        return (true, fullyResolved);
    }

    /// <summary>
    /// Protocol-agnostic check: does this content contain any source-domain WP URL?
    /// </summary>
    private bool HasSourceContentUrls(string content) =>
        !string.IsNullOrEmpty(content) && _hasSourceContentRegex.IsMatch(content);

    /// <summary>
    /// True if content still has source-domain media URLs after rewrite — means
    /// some media wasn't found in target library and target images will 404.
    /// </summary>
    private bool HasUnresolvedSourceMedia(string content) =>
        !string.IsNullOrEmpty(content) && _hasUnresolvedSourceMediaRegex.IsMatch(content);

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

        var targetPostsBySlug = BuildBySlug(_targetPosts, p => p.Slug, "target posts");
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
                var response = await SendWriteAsync(url, 0, () =>
                    _httpClient.SendAsync(CreateAuthenticatedRequest(HttpMethod.Delete, url)));
                if (response.IsSuccessStatusCode) _targetPosts.RemoveAll(p => p.Id == post.Id);
            }
            catch { /* skip */ }
        }

        progress.Complete($"Deleted {duplicates.Count} duplicate(s).");
    }

    private async Task FetchPostsInBulk(string apiUrl, List<WpPost> targetList, bool useAuth = false,
        string? fields = null)
    {
        // Index existing real-Id posts by Id so a fresh fetch REFRESHES them in place.
        // The old code skipped already-known Ids, which froze mutable fields (featured_media,
        // tags, categories, author, status) at whatever they were when the post was first
        // cached. For target that meant cached featured_media stayed 0 forever even after the
        // image was set live — so needsFeaturedRecheck fired for every post on every run and
        // the verified-cache fast path was never taken, making incremental migrate re-touch
        // nearly every post. Stub posts (Id==0, from backup) are still replaced by slug.
        var indexById = new Dictionary<int, int>();
        var stubsBySlug = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var idx = 0; idx < targetList.Count; idx++)
        {
            if (targetList[idx].Id > 0) indexById[targetList[idx].Id] = idx;
            else stubsBySlug.TryAdd(targetList[idx].Slug, idx);
        }

        var width = Term.Width;
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
                    // Already known by Id → overwrite in place so live values (featured_media,
                    // tags, …) replace the stale cached ones. Not counted as "new".
                    if (indexById.TryGetValue(post.Id, out var existingIdx))
                    {
                        targetList[existingIdx] = post;
                        continue;
                    }
                    if (stubsBySlug.TryGetValue(post.Slug, out var stubIndex))
                    {
                        targetList[stubIndex] = post;
                        stubsBySlug.Remove(post.Slug);
                        indexById[post.Id] = stubIndex;
                    }
                    else
                    {
                        targetList.Add(post);
                        indexById[post.Id] = targetList.Count - 1;
                    }
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
