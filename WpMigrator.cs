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
    private readonly ConcurrentBag<string> _uploadErrors = [];

    // Serialize re-auth on 401
    private readonly SemaphoreSlim _authLock = new(1, 1);

    // Global write throttle. WP REST POST triggers full save_post lifecycle
    // (RankMath sitemap regen, schema rebuild, revisions, plugin hooks) which
    // pile up under parallel load. Cap concurrency at 1 with a small spacing
    // delay so callers can stay parallel for fetches but writes serialize.
    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
    private long _lastWriteTicks;
    private const int MinWriteSpacingMs = 150;

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

    // Source media files known to be permanently unavailable (link rot: source WP
    // no longer serves the base file). Keyed by base filename (size-suffix stripped),
    // matching UploadMedia's lookupOriginal. Lets us (a) skip the slow live re-download
    // on every run and (b) treat posts whose only unresolved URLs are dead as fully
    // resolved, so they stop being re-examined. Persisted to dead-source-media.json.
    private readonly ConcurrentDictionary<string, bool> _deadSourceMedia = new(StringComparer.OrdinalIgnoreCase);

    // Source-domain regexes — built once per migrator, hit in the per-post hot path
    private readonly Regex _contentUrlRegex;
    private readonly Regex _hasSourceContentRegex;
    private readonly Regex _hasUnresolvedSourceMediaRegex;

    public WpMigrator(SiteConfig config)
    {
        _config = config;
        var escapedDomain = Regex.Escape(_config.SourceDomain);
        _contentUrlRegex = new Regex(
            @"https?://" + escapedDomain + @"/wp-content/uploads/[^\s""'<>\)]+",
            RegexOptions.Compiled);
        _hasSourceContentRegex = new Regex(
            @"https?://" + escapedDomain + @"/wp-content",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        _hasUnresolvedSourceMediaRegex = new Regex(
            @"https?://" + escapedDomain + @"/wp-content/uploads",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Recycle pooled sockets every 2 min so silently-killed idle connections
        // (Cloudflare / nginx LB closing without FIN) don't get reused and hang.
        // ConnectTimeout caps DNS+TLS so a stalled handshake doesn't burn the full timeout.
        var socketsHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        var retryHandler = new RetryHandler(socketsHandler)
        {
            MaxRetries = 3,
            ReauthAsync = ReauthAsync
        };
        // Per-request timeout: short enough to fail fast on hung WP requests,
        // long enough to cover legit slow operations (large content updates,
        // attachment uploads). RetryHandler covers transient failures.
        _httpClient = new HttpClient(retryHandler) { Timeout = TimeSpan.FromSeconds(90) };
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
        LoadDeadSourceMediaCache();
    }

    public async Task Migrate()
    {
        // Save verified-posts cache on Ctrl-C so a hang doesn't lose run progress.
        // Process keeps running long enough to flush, then exits via natural cancel.
        ConsoleCancelEventHandler? cancelHandler = (_, e) =>
        {
            try { SaveVerifiedPostsCache(); SaveTargetMediaCache(); SaveDeadSourceMediaCache(); } catch { /* best effort */ }
            Console.WriteLine("\n  Caches flushed on cancel.");
            // Surface upload errors on Ctrl-C too — otherwise an aborted run
            // hides exactly the diagnostics we need to see why files failed
            // to land on target.
            try { ReportUploadErrors(); } catch { /* best effort */ }
        };
        Console.CancelKeyPress += cancelHandler;

        try
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

        // Pull target body content so SyncAllPosts can tell verified-but-stale posts
        // (target body still pointing at source) apart from verified-and-clean ones.
        // Without this, the verified-cache shortcut hides posts whose body never got
        // its source URLs rewritten by an earlier run.
        Console.WriteLine("\n  Fetching target post content...");
        var targetContents = await FetchTargetContents();
        var targetContentsBySlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _targetPosts)
        {
            if (p.Id > 0 && targetContents.TryGetValue(p.Id, out var v))
                targetContentsBySlug[p.Slug] = v.Content;
        }
        Console.WriteLine($"  Got content for {targetContents.Count} target post(s).");

        // 4. Sync to target
        await SyncAuthors();
        BuildLookupDictionaries();
        await SyncTaxonomy();
        BuildLookupDictionaries();
        await SyncAllPosts(targetContentsBySlug);

        // Flush as soon as posts done — earlier crashes (dedupe) shouldn't lose verified cache.
        SaveVerifiedPostsCache();
        SaveTargetMediaCache();
        SaveDeadSourceMediaCache();

        // 5. Cleanup
        await FindAndDeleteDuplicates();
        SaveTargetMediaCache();
        SaveVerifiedPostsCache();
        SaveDeadSourceMediaCache();

        ReportUploadErrors();
        Console.WriteLine("\n  Migration complete!");
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
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
        foreach (var err in distinct)
            Console.WriteLine($"    - {err}");
    }

    public async Task FindCyrillicSlugs()
    {
        Console.WriteLine("\n  Fetching source post slugs...");
        var posts = await FetchAllPaginated<WpPost>(
            _config.SourceWpApiUrl, "posts", useAuth: false, extraQuery: "_fields=id,slug,title,link");
        Console.WriteLine($"  {posts.Count} source posts fetched.");

        var cyrillic = new Regex(@"\p{IsCyrillic}");
        var found = posts
            .Where(p => !string.IsNullOrEmpty(p.Slug) &&
                        cyrillic.IsMatch(Uri.UnescapeDataString(p.Slug)))
            .OrderBy(p => p.Slug)
            .ToList();

        if (found.Count == 0)
        {
            Console.WriteLine("\n  No Cyrillic slugs found in source posts.");
            return;
        }

        Console.WriteLine($"\n  {found.Count} source post(s) with Cyrillic slug:");
        foreach (var p in found)
        {
            var decoded = Uri.UnescapeDataString(p.Slug);
            var title = p.Title?.Rendered ?? "";
            Console.WriteLine($"    {decoded}  —  {title}");
            if (!string.IsNullOrEmpty(p.Link))
                Console.WriteLine($"      {p.Link}");
        }
    }

    public async Task FindExtraTargets()
    {
        Console.WriteLine("\n  Fetching source posts...");
        var sourcePosts = await FetchAllPaginated<WpPost>(
            _config.SourceWpApiUrl, "posts", useAuth: false, extraQuery: "_fields=id,slug,date,link");
        var sourceSlugs = sourcePosts.Select(p => p.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"  {sourcePosts.Count} source posts.");

        Console.WriteLine("  Fetching target posts...");
        var targetPosts = await FetchAllPaginated<WpPost>(
            _config.TargetWpApiUrl, "posts", useAuth: true, extraQuery: "_fields=id,slug,date,status,link");
        Console.WriteLine($"  {targetPosts.Count} target posts.");

        // Map each target to its "claimed" source slug (exact, or stripped of -N collision suffix).
        var stripSuffix = new Regex(@"-(\d{1,2})$");
        string? ClaimedSourceSlug(string targetSlug)
        {
            if (sourceSlugs.Contains(targetSlug)) return targetSlug;
            var m = stripSuffix.Match(targetSlug);
            if (!m.Success) return null;
            if (!int.TryParse(m.Groups[1].Value, out var n) || n < 2 || n > 19) return null;
            var stripped = targetSlug[..m.Index];
            return sourceSlugs.Contains(stripped) ? stripped : null;
        }

        var unclaimed = new List<WpPost>();
        var byClaim = new Dictionary<string, List<WpPost>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in targetPosts)
        {
            var claim = ClaimedSourceSlug(p.Slug);
            if (claim == null) { unclaimed.Add(p); continue; }
            if (!byClaim.TryGetValue(claim, out var list)) byClaim[claim] = list = [];
            list.Add(p);
        }

        var multiClaim = byClaim.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key).ToList();

        Console.WriteLine($"\n  Source: {sourcePosts.Count}, Target: {targetPosts.Count}, diff: {targetPosts.Count - sourcePosts.Count}");
        Console.WriteLine($"  Unclaimed targets (no source counterpart): {unclaimed.Count}");
        foreach (var p in unclaimed.OrderBy(p => p.Date))
            Console.WriteLine($"    id={p.Id}  {p.Date:yyyy-MM-dd}  {p.Slug}  {p.Link}");

        Console.WriteLine($"\n  Source slugs claimed by multiple targets: {multiClaim.Count}");
        foreach (var (sourceSlug, group) in multiClaim)
        {
            Console.WriteLine($"    source: {sourceSlug}");
            foreach (var p in group.OrderBy(p => p.Date))
                Console.WriteLine($"      id={p.Id}  {p.Date:yyyy-MM-dd}  {p.Slug}  {p.Link}");
        }
    }

    public async Task Status()
    {
        await FetchSourceData();
        BuildLookupDictionaries();
        await FetchTargetData();
        BuildLookupDictionaries();
        await PrintStatus();
    }

    /// <summary>
    /// Repro tool for server-side hang debugging. Hits a single target post with the
    /// minimal noop update (current author re-set to itself) and times each phase.
    /// While running, capture PHP-FPM slow log + MySQL SHOW PROCESSLIST on the server.
    /// </summary>
    public async Task HitPost(string slug)
    {
        await Authenticate();
        Console.WriteLine($"\n  Resolving slug '{slug}' on target...");

        var swFetch = System.Diagnostics.Stopwatch.StartNew();
        var url = $"{_config.TargetWpApiUrl}wp/v2/posts?slug={Uri.EscapeDataString(slug)}&_fields=id,slug,author&per_page=5";
        var resp = await _httpClient.SendAsync(CreateAuthenticatedRequest(HttpMethod.Get, url));
        var json = await resp.Content.ReadAsStringAsync();
        swFetch.Stop();
        Console.WriteLine($"  GET took {swFetch.ElapsedMilliseconds} ms, status {(int)resp.StatusCode}");
        if (!resp.IsSuccessStatusCode) { Console.WriteLine($"  Body: {json[..Math.Min(json.Length, 500)]}"); return; }

        var posts = JsonConvert.DeserializeObject<List<WpPost>>(json) ?? [];
        var match = posts.FirstOrDefault(p => p.Slug == slug);
        if (match == null) { Console.WriteLine("  No matching post."); return; }
        Console.WriteLine($"  Post id={match.Id} author={match.Author}");

        Console.WriteLine($"\n  POSTing noop author update (author={match.Author})...");
        var swPost = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await PostJsonAsync($"{_config.TargetWpApiUrl}wp/v2/posts/{match.Id}", new { author = match.Author });
            swPost.Stop();
            Console.WriteLine($"  POST took {swPost.ElapsedMilliseconds} ms — {(swPost.ElapsedMilliseconds > 5000 ? "SLOW" : "ok")}");
        }
        catch (Exception ex)
        {
            swPost.Stop();
            Console.WriteLine($"  POST failed after {swPost.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(_jwtToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
        return request;
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string url, object payload, bool noRetry = false)
    {
        var json = JsonConvert.SerializeObject(payload);
        return await SendWriteAsync(url, json.Length, () =>
        {
            var request = CreateAuthenticatedRequest(HttpMethod.Post, url);
            if (noRetry) request.Options.Set(RetryHandler.NoRetry, true);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return _httpClient.SendAsync(request);
        });
    }

    // Anything longer than this gets logged so we can correlate slow writes
    // to specific endpoints / payload sizes during a migrate run.
    private const int SlowWriteThresholdMs = 5000;

    /// <summary>
    /// Funnels every write (POST/PUT/PATCH/DELETE/multipart upload) through a
    /// single semaphore + spacing delay so concurrent callers can't pile up
    /// save_post hooks on the WP server. Logs writes exceeding SlowWriteThresholdMs.
    /// </summary>
    private async Task<HttpResponseMessage> SendWriteAsync(string url, int payloadBytes, Func<Task<HttpResponseMessage>> sendFunc)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            var prev = Interlocked.Read(ref _lastWriteTicks);
            var elapsedMs = (DateTime.UtcNow.Ticks - prev) / TimeSpan.TicksPerMillisecond;
            if (elapsedMs < MinWriteSpacingMs)
                await Task.Delay((int)(MinWriteSpacingMs - elapsedMs));
            Interlocked.Exchange(ref _lastWriteTicks, DateTime.UtcNow.Ticks);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await sendFunc();
            sw.Stop();
            if (sw.ElapsedMilliseconds >= SlowWriteThresholdMs)
            {
                var status = (int)response.StatusCode;
                Console.WriteLine($"\n  [slow-write] {sw.ElapsedMilliseconds}ms {status} {payloadBytes}B {ShortenUrl(url)}");
            }
            return response;
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private string ShortenUrl(string url)
    {
        // Strip the WP REST prefix so the log line is readable
        if (url.StartsWith(_config.TargetWpApiUrl, StringComparison.OrdinalIgnoreCase))
            return "T:" + url[_config.TargetWpApiUrl.Length..];
        if (url.StartsWith(_config.SourceWpApiUrl, StringComparison.OrdinalIgnoreCase))
            return "S:" + url[_config.SourceWpApiUrl.Length..];
        return url;
    }

    private const int SlowReadThresholdMs = 5000;

    /// <summary>
    /// Fetch one page from a WP REST endpoint. Returns items and the X-WP-TotalPages value.
    /// Logs reads exceeding SlowReadThresholdMs for hang diagnostics.
    /// </summary>
    private async Task<(List<T> Items, int TotalPages)> FetchPageAsync<T>(
        string apiUrl, string endpoint, int page, bool useAuth = false, string? extraQuery = null)
    {
        var url = $"{apiUrl}wp/v2/{endpoint}?per_page=100&page={page}";
        if (!string.IsNullOrEmpty(extraQuery)) url += $"&{extraQuery}";

        // Reads are idempotent GETs, so retry transient failures (5xx / timeout) instead of
        // silently returning ([], 0). On this slow target a single dropped page meant ~100
        // media items never entered the in-memory index, so their content URLs could never
        // be rewritten — the post stayed "unresolved" and was re-pushed on every run (the
        // main reason already-migrated posts never converged). 4xx is terminal (e.g. a page
        // past the real last page), so it isn't retried.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var response = useAuth
                    ? await _httpClient.SendAsync(CreateAuthenticatedRequest(HttpMethod.Get, url))
                    : await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    sw.Stop();
                    var code = (int)response.StatusCode;
                    if (code >= 500 && attempt < maxAttempts)
                    {
                        Console.WriteLine($"\n  [read-retry {attempt}/{maxAttempts}] {code} {ShortenUrl(url)}");
                        await Task.Delay(attempt * 1000);
                        continue;
                    }
                    if (sw.ElapsedMilliseconds >= SlowReadThresholdMs || code >= 500)
                        Console.WriteLine($"\n  [slow-read] {sw.ElapsedMilliseconds}ms {code} {ShortenUrl(url)}");
                    return ([], 0);
                }

                var totalPages = 1;
                if (response.Headers.TryGetValues("X-WP-TotalPages", out var values) &&
                    int.TryParse(values.FirstOrDefault(), out var tp))
                    totalPages = tp;

                var json = await response.Content.ReadAsStringAsync();
                sw.Stop();
                if (sw.ElapsedMilliseconds >= SlowReadThresholdMs)
                    Console.WriteLine($"\n  [slow-read] {sw.ElapsedMilliseconds}ms {(int)response.StatusCode} {json.Length}B {ShortenUrl(url)}");

                var items = JsonConvert.DeserializeObject<List<T>>(json) ?? [];
                return (items, totalPages);
            }
            catch (Exception ex)
            {
                sw.Stop();
                if (attempt < maxAttempts)
                {
                    Console.WriteLine($"\n  [read-retry {attempt}/{maxAttempts}] {ex.GetType().Name} {ShortenUrl(url)}");
                    await Task.Delay(attempt * 1000);
                    continue;
                }
                Console.WriteLine($"\n  [read-fail] {sw.ElapsedMilliseconds}ms {ex.GetType().Name} {ShortenUrl(url)}: {ex.Message}");
                return ([], 0);
            }
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
            // noRetry: a create whose response times out may still have committed on the
            // server — retrying it mints a duplicate (ghost posts with "-2" slugs, dup
            // tags). A genuinely failed create is recovered next run via slug matching.
            var response = await PostJsonAsync($"{_config.TargetWpApiUrl}wp/v2/{endpoint}", payload, noRetry: true);
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

        // Slug keys must survive percent-encoding differences between installs: a target
        // slug can be stored URL-encoded ("%d0%b2%d1%80") while the matching source slug is
        // the decoded form ("вр"). Keying by the raw slug made every Cyrillic-tagged post
        // mismatch and re-push its tags/categories on every run (and never converge).
        // SlugComparer normalizes both sides via Uri.UnescapeDataString. Last-wins loop
        // (not ToDictionary) so two raw slugs that normalize to the same key don't throw.
        _targetTagsDict = new Dictionary<string, WpTag>(SlugComparer.Instance);
        foreach (var t in _targetTags) _targetTagsDict[t.Slug] = t;
        _targetCategoriesDict = new Dictionary<string, WpCategory>(SlugComparer.Instance);
        foreach (var c in _targetCategories) _targetCategoriesDict[c.Slug] = c;

        _targetAuthorsBySlug = _targetAuthors.ToDictionary(a => a.Slug, StringComparer.OrdinalIgnoreCase);
        _targetAuthorsByName = _targetAuthors
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Equality over WP slugs that ignores percent-encoding and case, so a decoded
    /// source slug ("вр") matches a URL-encoded target slug ("%d0%b2%d1%80"). Used to
    /// key the target tag/category lookups; without it, non-ASCII taxonomy never matched
    /// and posts were re-pushed forever.
    /// </summary>
    private sealed class SlugComparer : IEqualityComparer<string>
    {
        public static readonly SlugComparer Instance = new();

        private static string Norm(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Uri.UnescapeDataString(s).ToLowerInvariant(); }
            catch { return s.ToLowerInvariant(); }
        }

        public bool Equals(string? a, string? b) => Norm(a) == Norm(b);
        public int GetHashCode(string s) => Norm(s).GetHashCode();
    }

    // Keys can collide on slug: WP rejects same-slug create but historical
    // dirty data (manual edits, failed migration retries, dedupe gaps) can
    // leave two posts sharing a slug. First-wins + warn so the caller sees
    // which slugs are dup without crashing the run.
    private static Dictionary<string, T> BuildBySlug<T>(IEnumerable<T> items, Func<T, string?> getSlug, string label)
    {
        var dict = new Dictionary<string, T>(StringComparer.Ordinal);
        var dups = new List<string>();
        foreach (var item in items)
        {
            var slug = getSlug(item);
            if (string.IsNullOrEmpty(slug)) continue;
            if (!dict.TryAdd(slug, item)) dups.Add(slug);
        }
        if (dups.Count > 0)
        {
            var sample = string.Join(", ", dups.Distinct().Take(5));
            var more = dups.Distinct().Count() > 5 ? "…" : "";
            Console.WriteLine($"    Warning: {dups.Count} duplicate slug(s) in {label}, kept first: {sample}{more}");
        }
        return dict;
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

        // Match source-domain media URLs across both http and https (legacy posts may
        // still embed http URLs even when current site is https-only).
        return _contentUrlRegex.Replace(content, match =>
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

    /// <summary>
    /// True if content has at least one source-domain media URL that is NOT known-dead —
    /// i.e. something this run could still upload and rewrite. Returns false when the only
    /// remaining source URLs are permanent link rot (cached in _deadSourceMedia), so such
    /// posts can be marked verified instead of re-examined (and re-pushed) every run.
    /// </summary>
    private bool HasFixableSourceMedia(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        foreach (Match m in _contentUrlRegex.Matches(content))
            if (!_deadSourceMedia.ContainsKey(DeadMediaKey(m.Value)))
                return true;
        return false;
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
