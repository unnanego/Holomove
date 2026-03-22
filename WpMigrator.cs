using System.Net.Http.Headers;
using System.Text;
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

    // Target media lookup
    private readonly Dictionary<string, MediaItem> _targetMediaByUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MediaItem> _targetMediaByFilename = new(StringComparer.OrdinalIgnoreCase);

    public WpMigrator(SiteConfig config)
    {
        _config = config;
        var retryHandler = new RetryHandler(new HttpClientHandler()) { MaxRetries = 3 };
        _httpClient = new HttpClient(retryHandler) { Timeout = TimeSpan.FromMinutes(30) };
    }

    public async Task Init()
    {
        await Authenticate();
        InitBackup();
        LoadSourcePostsFromBackup();
        LoadTargetPostCache();
    }

    public async Task Migrate()
    {
        // 1. Fetch source data
        await FetchSourceData();
        BuildLookupDictionaries();

        // 2. Save to local backup
        Console.WriteLine("\n  Saving to local backup...");
        var backupProgress = new ProgressBar();
        for (var i = 0; i < _sourcePosts.Count; i++)
        {
            var post = _sourcePosts[i];
            backupProgress.Update(i + 1, _sourcePosts.Count, post.Slug);
            if (!PostExistsInBackup(post))
                await SavePostToBackup(post);
        }
        backupProgress.Complete($"Backed up {_sourcePosts.Count} post(s).");
        await SaveAuthorsToBackup();
        await SaveTaxonomyToBackup();
        await SaveMetadataToBackup();

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

        Console.WriteLine("\n  Migration complete!");
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

    private async Task<List<T>> FetchAllPaginated<T>(string apiUrl, string endpoint, bool useAuth = false)
    {
        var all = new List<T>();
        var page = 1;

        while (true)
        {
            try
            {
                var url = $"{apiUrl}wp/v2/{endpoint}?per_page=100&page={page}";

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
                var items = JsonConvert.DeserializeObject<List<T>>(json) ?? [];
                if (items.Count == 0) break;

                all.AddRange(items);
                page++;
            }
            catch { break; }
        }

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
