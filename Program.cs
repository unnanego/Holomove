using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WordPressPCL;
using WordPressPCL.Models;

var html = new HtmlDocument();

// Source: WordPress
var oldWP = new WordPressClient("https://holographica.space/wp-json");

// Target: Ghost at new.holographica.space
const string ghostUrl = "https://new.holographica.space";
const string ghostAdminApiKey = "YOUR_ADMIN_API_KEY"; // Format: {id}:{secret} - Get from Ghost Admin > Settings > Integrations

// Ghost API client setup
var ghostClient = new GhostAdminClient(ghostUrl, ghostAdminApiKey);

List<Post>? oldPosts = null;
List<Tag>? oldTags = null;
List<User>? oldUsers = null;
List<Category>? oldCategories = null;
List<GhostPost>? ghostPosts = null;
List<GhostTag>? ghostTags = null;

var oldTagsDict = new Dictionary<int, string>();
var oldUsersDict = new Dictionary<int, string>();
var ghostTagsDict = new Dictionary<string, string>(); // slug -> ghost id

// await LoadLocalData();
await DownloadAndSaveAllData();
await ProcessPosts();

async Task LoadLocalData()
{
    oldPosts = JsonConvert.DeserializeObject<List<Post>>(await File.ReadAllTextAsync("oldPosts.txt"));
    oldTags = JsonConvert.DeserializeObject<List<Tag>>(await File.ReadAllTextAsync("oldTags.txt"));
    oldUsers = JsonConvert.DeserializeObject<List<User>>(await File.ReadAllTextAsync("oldUsers.txt"));
    oldCategories = JsonConvert.DeserializeObject<List<Category>>(await File.ReadAllTextAsync("oldCategories.txt"));
    ghostPosts = JsonConvert.DeserializeObject<List<GhostPost>>(await File.ReadAllTextAsync("ghostPosts.txt"));
    ghostTags = JsonConvert.DeserializeObject<List<GhostTag>>(await File.ReadAllTextAsync("ghostTags.txt"));
}

async Task DownloadAndSaveAllData()
{
    Console.WriteLine("Downloading WordPress data...");
    oldPosts = (await oldWP.Posts.GetAllAsync()).ToList();
    oldUsers = (await oldWP.Users.GetAllAsync()).ToList();
    oldTags = (await oldWP.Tags.GetAllAsync()).ToList();
    oldCategories = (await oldWP.Categories.GetAllAsync()).ToList();

    Console.WriteLine("Downloading Ghost data...");
    ghostPosts = await ghostClient.GetAllPostsAsync();
    ghostTags = await ghostClient.GetAllTagsAsync();

    await File.WriteAllTextAsync("oldUsers.txt", JsonConvert.SerializeObject(oldUsers));
    await File.WriteAllTextAsync("oldTags.txt", JsonConvert.SerializeObject(oldTags));
    await File.WriteAllTextAsync("oldPosts.txt", JsonConvert.SerializeObject(oldPosts));
    await File.WriteAllTextAsync("oldCategories.txt", JsonConvert.SerializeObject(oldCategories));
    await File.WriteAllTextAsync("ghostPosts.txt", JsonConvert.SerializeObject(ghostPosts));
    await File.WriteAllTextAsync("ghostTags.txt", JsonConvert.SerializeObject(ghostTags));

    Console.WriteLine($"Downloaded {oldPosts.Count} WP posts, {ghostPosts.Count} Ghost posts");
}

async Task ProcessPosts()
{
    // Build lookup dictionaries
    if (oldTags != null)
        foreach (var tag in oldTags)
            oldTagsDict[tag.Id] = tag.Slug;

    if (oldUsers != null)
        foreach (var user in oldUsers)
            oldUsersDict[user.Id] = user.Slug;

    if (ghostTags != null)
        foreach (var tag in ghostTags)
            ghostTagsDict[tag.Slug] = tag.Id;

    if (oldPosts == null) return;

    var total = oldPosts.Count;
    var processed = 0;

    foreach (var oldPost in oldPosts)
    {
        processed++;
        var progress = (processed / (decimal)total * 100).ToString("F2");
        Console.Write($"Progress {progress}% ");

        var targetSlug = $"{oldPost.Slug}-{oldPost.Id}";

        if (ghostPosts?.Any(p => p.Slug == targetSlug) == true)
        {
            Console.WriteLine($"Post already exists: {targetSlug}");
            continue;
        }

        await MovePostToGhost(oldPost, targetSlug);
    }
}

async Task MovePostToGhost(Post wpPost, string targetSlug)
{
    Console.WriteLine($"Creating post: {targetSlug}");

    // Convert WP tags to Ghost tags
    var postTags = new List<GhostTag>();
    foreach (var tagId in wpPost.Tags)
    {
        if (!oldTagsDict.TryGetValue(tagId, out var slug)) continue;

        var tagName = oldTags?.FirstOrDefault(t => t.Id == tagId)?.Name ?? slug;

        if (ghostTagsDict.TryGetValue(slug, out var ghostTagId))
        {
            postTags.Add(new GhostTag { Id = ghostTagId, Slug = slug, Name = tagName });
        }
        else
        {
            // Create new tag in Ghost
            var newTag = await ghostClient.CreateTagAsync(slug, tagName);
            if (newTag == null) continue;
            ghostTags?.Add(newTag);
            ghostTagsDict[newTag.Slug] = newTag.Id;
            postTags.Add(newTag);
        }
    }

    // Also convert categories to tags (Ghost doesn't have categories)
    foreach (var category in wpPost.Categories.Select(catId => oldCategories?.FirstOrDefault(c => c.Id == catId)).Where(category => category != null))
    {
        if (ghostTagsDict.TryGetValue(category!.Slug, out var ghostTagId))
        {
            postTags.Add(new GhostTag { Id = ghostTagId, Slug = category.Slug, Name = category.Name });
        }
        else
        {
            var newTag = await ghostClient.CreateTagAsync(category.Slug, category.Name);
            if (newTag == null) continue;
            ghostTags?.Add(newTag);
            ghostTagsDict[newTag.Slug] = newTag.Id;
            postTags.Add(newTag);
        }
    }

    // Process images in content
    var content = wpPost.Content.Rendered;
    string? featureImage = null;

    html.LoadHtml(content);
    var imgNodes = html.DocumentNode.SelectNodes("//img");
    foreach (var src in imgNodes.Select(node => node.GetAttributeValue("src", string.Empty)).Where(src => !string.IsNullOrEmpty(src)))
    {
        try
        {
            // Upload image to Ghost
            var imageUrl = await ghostClient.UploadImageAsync(src);
            if (string.IsNullOrEmpty(imageUrl)) continue;
            
            content = content.Replace(src, imageUrl);
            featureImage ??= imageUrl; // First image becomes featured
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to upload image {src}: {ex.Message}");
        }
    }

    // Create Ghost post
    var ghostPost = new GhostPost
    {
        Title = wpPost.Title.Rendered,
        Slug = targetSlug,
        Html = content,
        CustomExcerpt = wpPost.Excerpt.Rendered,
        Status = wpPost.Status == Status.Publish ? "published" : "draft",
        PublishedAt = wpPost.Date,
        Tags = postTags,
        FeatureImage = featureImage
    };

    var created = await ghostClient.CreatePostAsync(ghostPost);
    if (created != null)
    {
        ghostPosts?.Add(created);
        Console.WriteLine($"Created: {created.Slug}");
    }
}

// Ghost API client
public class GhostAdminClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _keyId;
    private readonly byte[] _keySecret;

    public GhostAdminClient(string baseUrl, string adminApiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();

        var parts = adminApiKey.Split(':');
        _keyId = parts[0];
        _keySecret = Convert.FromHexString(parts[1]);
    }

    private string GenerateToken()
    {
        var now = DateTime.UtcNow;
        var handler = new JwtSecurityTokenHandler();

        var descriptor = new SecurityTokenDescriptor
        {
            Audience = "/admin/",
            IssuedAt = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(_keySecret),
                SecurityAlgorithms.HmacSha256Signature),
            AdditionalHeaderClaims = new Dictionary<string, object>
            {
                { "kid", _keyId },
                { "typ", "JWT" }
            }
        };

        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, $"{_baseUrl}/ghost/api/admin/{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Ghost", GenerateToken());
        return request;
    }

    public async Task<List<GhostPost>> GetAllPostsAsync()
    {
        var posts = new List<GhostPost>();
        var page = 1;

        while (true)
        {
            var request = CreateRequest(HttpMethod.Get, $"posts/?limit=100&page={page}");
            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error getting posts: {json}");
                break;
            }

            var result = JObject.Parse(json);
            var pagePosts = result["posts"]?.ToObject<List<GhostPost>>() ?? new List<GhostPost>();

            if (pagePosts.Count == 0) break;

            posts.AddRange(pagePosts);
            page++;
        }

        return posts;
    }

    public async Task<List<GhostTag>> GetAllTagsAsync()
    {
        var request = CreateRequest(HttpMethod.Get, "tags/?limit=all");
        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error getting tags: {json}");
            return [];
        }

        var result = JObject.Parse(json);
        return result["tags"]?.ToObject<List<GhostTag>>() ?? new List<GhostTag>();
    }

    public async Task<GhostTag?> CreateTagAsync(string slug, string name)
    {
        var request = CreateRequest(HttpMethod.Post, "tags/");
        var payload = new { tags = new[] { new { slug, name } } };
        request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error creating tag {slug}: {json}");
            return null;
        }

        var result = JObject.Parse(json);
        return result["tags"]?[0]?.ToObject<GhostTag>();
    }

    public async Task<GhostPost?> CreatePostAsync(GhostPost post)
    {
        var request = CreateRequest(HttpMethod.Post, "posts/?source=html");

        var postData = new Dictionary<string, object?>
        {
            ["title"] = post.Title,
            ["slug"] = post.Slug,
            ["html"] = post.Html,
            ["status"] = post.Status,
            ["custom_excerpt"] = post.CustomExcerpt,
            ["published_at"] = post.PublishedAt?.ToString("o"),
            ["feature_image"] = post.FeatureImage,
            ["tags"] = post.Tags?.Select(t => new { id = t.Id, slug = t.Slug, name = t.Name }).ToArray()
        };

        var payload = new { posts = new[] { postData } };
        request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error creating post {post.Slug}: {json}");
            return null;
        }

        var result = JObject.Parse(json);
        return result["posts"]?[0]?.ToObject<GhostPost>();
    }

    public async Task<string?> UploadImageAsync(string imageUrl)
    {
        try
        {
            // Download image
            var imageBytes = await _http.GetByteArrayAsync(imageUrl);
            var fileName = Path.GetFileName(new Uri(imageUrl).LocalPath);
            fileName = Regex.Replace(fileName, @"[^\u0000-\u007F]+", "");
            if (fileName.Length > 100) fileName = fileName[..100] + Path.GetExtension(fileName);

            // Upload to Ghost
            var request = CreateRequest(HttpMethod.Post, "images/upload/");

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
            content.Add(fileContent, "file", fileName);

            request.Content = content;

            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Image upload failed: {json}");
                return null;
            }

            var result = JObject.Parse(json);
            return result["images"]?[0]?["url"]?.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Image download/upload error: {ex.Message}");
            return null;
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
            _ => "application/octet-stream"
        };
    }
}

// Ghost models
public class GhostPost
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("title")] public string Title { get; set; } = "";
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("html")] public string? Html { get; set; }
    [JsonProperty("custom_excerpt")] public string? CustomExcerpt { get; set; }
    [JsonProperty("status")] public string Status { get; set; } = "draft";
    [JsonProperty("published_at")] public DateTime? PublishedAt { get; set; }
    [JsonProperty("feature_image")] public string? FeatureImage { get; set; }
    [JsonProperty("tags")] public List<GhostTag>? Tags { get; set; }
}

public class GhostTag
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
}