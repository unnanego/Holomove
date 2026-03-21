using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using WordPressPCL;
using WordPressPCL.Models;

namespace Holomove;

public partial class WpMigrator
{
    private readonly string _dataFolder;
    private readonly WordPressClient _sourceWp;
    private readonly WordPressClient _targetWp;
    private readonly HttpClient _httpClient;
    private string? _jwtToken;

    // Data collections
    private List<Post> _sourcePosts = [];
    private List<Tag> _sourceTags = [];
    private List<User> _sourceAuthors = [];
    private List<Category> _sourceCategories = [];
    private List<Post> _targetPosts = [];
    private List<Tag> _targetTags = [];
    private List<User> _targetAuthors = [];
    private List<Category> _targetCategories = [];

    // Lookup dictionaries
    private Dictionary<int, Tag> _sourceTagsDict = new();
    private Dictionary<int, User> _sourceUsersDict = new();
    private Dictionary<int, Category> _sourceCategoriesDict = new();
    private Dictionary<string, Tag> _targetTagsDict = new();
    private Dictionary<string, User> _targetAuthorsDict = new();
    private Dictionary<string, Category> _targetCategoriesDict = new();

    // Target media lookup
    private readonly Dictionary<string, MediaItem> _targetMediaByUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MediaItem> _targetMediaByFilename = new(StringComparer.OrdinalIgnoreCase);

    public WpMigrator()
    {
        _dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "downloads");
        Directory.CreateDirectory(_dataFolder);

        _sourceWp = new WordPressClient(Config.SourceWpApiUrl);
        _targetWp = new WordPressClient(Config.TargetWpApiUrl);

        var retryHandler = new RetryHandler(new HttpClientHandler()) { MaxRetries = 3 };
        _httpClient = new HttpClient(retryHandler) { Timeout = TimeSpan.FromMinutes(30) };
    }

    public async Task Init()
    {
        await Authenticate();
        await LoadCache();
    }

    public async Task Migrate()
    {
        await DownloadTargetMediaList();
        await DownloadAllPostsAndCompare();
        await MigrateAuthors();
        await ProcessPosts();
        await SaveCache();
        Console.WriteLine("\nMigration complete!");
    }

    public async Task Status()
    {
        await DownloadAllPostsAndCompare();
    }

    public async Task FixDuplicates()
    {
        await DownloadAllPostsAndCompare();
        await FindAndDeleteDuplicates();
        await SaveCache();
    }

    public async Task FixAuthors()
    {
        await DownloadAllPostsAndCompare();
        await MigrateAuthors();
        await UpdateExistingPostsAuthors();
        await SaveCache();
    }

    public async Task FixMedia()
    {
        await DownloadTargetMediaList();
        await DownloadAllPostsAndCompare();
        await VerifyAndFixExistingPostsMedia();
        await SaveCache();
    }

    public async Task FixAll()
    {
        await DownloadTargetMediaList();
        await DownloadAllPostsAndCompare();
        await FindAndDeleteDuplicates();
        await MigrateAuthors();
        await UpdateExistingPostsAuthors();
        await VerifyAndFixExistingPostsMedia();
        await SaveCache();
        Console.WriteLine("\nAll fixes complete!");
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(_jwtToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
        }
        return request;
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string url, object payload)
    {
        var request = CreateAuthenticatedRequest(HttpMethod.Post, url);
        request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        return await _httpClient.SendAsync(request);
    }

    private async Task<List<T>?> FetchAllFromApi<T>(string endpoint)
    {
        try
        {
            var request = CreateAuthenticatedRequest(HttpMethod.Get, $"{Config.TargetWpApiUrl}wp/v2/{endpoint}?per_page=100");
            var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode) return JsonConvert.DeserializeObject<List<T>>(json);
            
            Console.WriteLine($"  Error fetching {endpoint}: {json}");
            return null;

        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error fetching {endpoint}: {ex.Message}");
            return null;
        }
    }

    private void BuildLookupDictionaries()
    {
        _sourceTagsDict = _sourceTags.ToDictionary(t => t.Id);
        _sourceUsersDict = _sourceAuthors.ToDictionary(u => u.Id);
        _sourceCategoriesDict = _sourceCategories.ToDictionary(c => c.Id);
        _targetTagsDict = _targetTags.ToDictionary(t => t.Slug);
        _targetAuthorsDict = _targetAuthors.ToDictionary(u => u.Slug);
        _targetCategoriesDict = _targetCategories.ToDictionary(c => c.Slug);
    }

    private static string GetBaseSlug(string slug)
    {
        var match = DuplicateSlugSuffixRegex().Match(slug);
        return match.Success ? match.Groups[1].Value : slug;
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

    [GeneratedRegex("^(.+)-([2-9]|1[0-9])$")]
    private static partial Regex DuplicateSlugSuffixRegex();
}
