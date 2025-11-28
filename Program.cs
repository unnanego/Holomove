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
var wp = new WordPressClient("https://holographica.space/wp-json/");
// Target: Ghost at new.holographica.space
const string ghostUrl = "https://new.holographica.space";
const string ghostAdminApiKey = "69288e148eb69d0351ab7933:0f24e27f99e1af24e71a4817d8783636f9b808301cb99896c640a435ec0a03c4"; // Format: {id}:{secret} - Get from Ghost Admin > Settings > Integrations

// Ghost API client setup
var ghostClient = new GhostAdminClient(ghostUrl, ghostAdminApiKey);

List<Post>? wpPosts = null;
List<Tag>? wpTags = null;
List<User>? wpUsers = null;
List<Category>? wpCategories = null;
List<GhostPost>? ghostPosts = null;
List<GhostTag>? ghostTags = null;

var wpTagsDict = new Dictionary<int, string>();
var wpUsersDict = new Dictionary<int, string>();
var ghostTagsDict = new Dictionary<string, string>(); // slug -> ghost id

// await LoadLocalData();
await DownloadAndSaveAllData();
// await ProcessPosts();
await ProcessFivePosts();
await GenerateRedirects();

async Task LoadLocalData()
{
    if (File.Exists("wpPosts.txt"))
        wpPosts = JsonConvert.DeserializeObject<List<Post>>(await File.ReadAllTextAsync("wpPosts.txt"));
    if (File.Exists("wpTags.txt"))
        wpTags = JsonConvert.DeserializeObject<List<Tag>>(await File.ReadAllTextAsync("wpTags.txt"));
    if (File.Exists("wpUsers.txt"))
        wpUsers = JsonConvert.DeserializeObject<List<User>>(await File.ReadAllTextAsync("wpUsers.txt"));
    if (File.Exists("wpCategories.txt"))
        wpCategories = JsonConvert.DeserializeObject<List<Category>>(await File.ReadAllTextAsync("wpCategories.txt"));
    if (File.Exists("ghostPosts.txt"))
        ghostPosts = JsonConvert.DeserializeObject<List<GhostPost>>(await File.ReadAllTextAsync("ghostPosts.txt"));
    if (File.Exists("ghostTags.txt"))
        ghostTags = JsonConvert.DeserializeObject<List<GhostTag>>(await File.ReadAllTextAsync("ghostTags.txt"));
}

async Task DownloadAndSaveAllData()
{
    // Load existing data if available
    var existingPostIds = new HashSet<int>();
    if (File.Exists("wpPosts.txt"))
    {
        var existingPosts = JsonConvert.DeserializeObject<List<Post>>(await File.ReadAllTextAsync("wpPosts.txt"));
        if (existingPosts != null)
        {
            wpPosts = existingPosts;
            foreach (var p in existingPosts)
                existingPostIds.Add(p.Id);
            Console.WriteLine($"Loaded {existingPostIds.Count} existing WP posts from cache");
        }
    }

    wpPosts ??= [];

    // First, fetch only IDs to check what's new (minimal data transfer)
    Console.WriteLine("Checking for new WordPress posts...");
    using var http = new HttpClient();
    var page = 1;
    var newPostIds = new List<int>();

    while (true)
    {
        try
        {
            var url = $"https://holographica.space/wp-json/wp/v2/posts?_fields=id&per_page=100&page={page}";
            var response = await http.GetAsync(url);

            if (!response.IsSuccessStatusCode) break; // No more pages

            var json = await response.Content.ReadAsStringAsync();
            var ids = JsonConvert.DeserializeObject<List<IdOnly>>(json) ?? [];

            if (ids.Count == 0) break;

            foreach (var item in ids)
            {
                if (!existingPostIds.Contains(item.Id))
                    newPostIds.Add(item.Id);
            }

            page++;
        }
        catch
        {
            break; // Error fetching page, stop
        }
    }

    Console.WriteLine($"Found {newPostIds.Count} new posts");

    // Download only new posts
    if (newPostIds.Count > 0)
    {
        Console.WriteLine("Downloading new posts...");
        var downloaded = 0;
        foreach (var id in newPostIds)
        {
            downloaded++;
            Console.Write($"\r  Downloading post {downloaded}/{newPostIds.Count}...");
            var post = await wp.Posts.GetByIDAsync(id);
            wpPosts.Add(post);
        }
        Console.WriteLine($"\r  Downloaded {newPostIds.Count} new posts              ");
    }

    wpUsers = (await wp.Users.GetAllAsync()).ToList();
    wpTags = (await wp.Tags.GetAllAsync()).ToList();
    wpCategories = (await wp.Categories.GetAllAsync()).ToList();

    Console.WriteLine("Downloading Ghost data...");
    ghostPosts = await ghostClient.GetAllPostsAsync();
    ghostTags = await ghostClient.GetAllTagsAsync();

    await File.WriteAllTextAsync("wpUsers.txt", JsonConvert.SerializeObject(wpUsers));
    await File.WriteAllTextAsync("wpTags.txt", JsonConvert.SerializeObject(wpTags));
    await File.WriteAllTextAsync("wpPosts.txt", JsonConvert.SerializeObject(wpPosts));
    await File.WriteAllTextAsync("wpCategories.txt", JsonConvert.SerializeObject(wpCategories));
    await File.WriteAllTextAsync("ghostPosts.txt", JsonConvert.SerializeObject(ghostPosts));
    await File.WriteAllTextAsync("ghostTags.txt", JsonConvert.SerializeObject(ghostTags));

    Console.WriteLine($"Saved {wpPosts.Count} WP posts, {ghostPosts.Count} Ghost posts");
}

async Task ProcessPosts()
{
    BuildLookupDictionaries();
    if (wpPosts == null) return;

    var total = wpPosts.Count;
    var processed = 0;

    foreach (var wpPost in wpPosts)
    {
        processed++;
        var progress = (processed / (decimal)total * 100).ToString("F2");
        Console.Write($"Progress {progress}% ");

        var targetSlug = wpPost.Slug;

        if (ghostPosts?.Any(p => p.Slug == targetSlug) == true)
        {
            Console.WriteLine($"Post already exists: {targetSlug}");
            continue;
        }

        await MovePostToGhost(wpPost, targetSlug);
    }
}

async Task ProcessFivePosts()
{
    BuildLookupDictionaries();
    if (wpPosts == null) return;

    var postsToProcess = wpPosts
        .Where(p => ghostPosts?.Any(g => g.Slug == p.Slug) != true)
        .Take(5)
        .ToList();

    Console.WriteLine($"Processing {postsToProcess.Count} posts...");

    foreach (var wpPost in postsToProcess)
    {
        await MovePostToGhost(wpPost, wpPost.Slug);
    }
}

void BuildLookupDictionaries()
{
    if (wpTags != null)
        foreach (var tag in wpTags)
            wpTagsDict[tag.Id] = tag.Slug;

    if (wpUsers != null)
        foreach (var user in wpUsers)
            wpUsersDict[user.Id] = user.Slug;

    if (ghostTags != null)
        foreach (var tag in ghostTags)
            ghostTagsDict[tag.Slug] = tag.Id;
}

async Task GenerateRedirects()
{
    BuildLookupDictionaries();
    if (wpPosts == null || wpCategories == null) return;

    var redirects = new List<object>();

    foreach (var post in wpPosts)
    {
        var categoryId = post.Categories.FirstOrDefault();
        var category = wpCategories.FirstOrDefault(c => c.Id == categoryId);
        var categorySlug = category?.Slug ?? "uncategorized";

        // WordPress URL: /category/post-slug/
        // Ghost URL: /post-slug/
        redirects.Add(new
        {
            from = $"/{categorySlug}/{post.Slug}/",
            to = $"/{post.Slug}/",
            permanent = true
        });
    }

    var json = JsonConvert.SerializeObject(redirects, Formatting.Indented);
    await File.WriteAllTextAsync("redirects.json", json);
    Console.WriteLine($"Generated {redirects.Count} redirects to redirects.json");
}

async Task MovePostToGhost(Post wpPost, string targetSlug)
{
    Console.WriteLine($"Creating post: {targetSlug}");

    // Convert WP tags to Ghost tags
    var postTags = new List<GhostTag>();
    foreach (var tagId in wpPost.Tags)
    {
        if (!wpTagsDict.TryGetValue(tagId, out var slug)) continue;

        var tagName = wpTags?.FirstOrDefault(t => t.Id == tagId)?.Name ?? slug;

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
    foreach (var category in wpPost.Categories.Select(catId => wpCategories?.FirstOrDefault(c => c.Id == catId)).Where(category => category != null))
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
    string? featureImageSrc = null;

    html.LoadHtml(content);
    var imgNodes = html.DocumentNode.SelectNodes("//img");
    foreach (var imgNode in imgNodes)
    {
        var src = imgNode.GetAttributeValue("src", string.Empty);
        if (string.IsNullOrEmpty(src)) continue;

        try
        {
            // Upload image to Ghost
            var uploadedUrl = await ghostClient.UploadImageAsync(src);
            if (string.IsNullOrEmpty(uploadedUrl)) continue;

            // First image becomes featured image
            if (featureImage == null)
            {
                featureImage = uploadedUrl;
                featureImageSrc = src;
            }
            else
            {
                // Update other images in content with new URL
                content = content.Replace(src, uploadedUrl);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to upload image {src}: {ex.Message}");
        }
    }

    // Remove the featured image from content to avoid duplication
    if (featureImageSrc != null)
    {
        html.LoadHtml(content);
        var featureImgNode = html.DocumentNode.SelectSingleNode($"//img[@src='{featureImageSrc}']");
        {
            // Remove the img tag and its parent if it's a figure or empty p/div
            var parent = featureImgNode.ParentNode;
            featureImgNode.Remove();
            if (parent.Name == "figure" || (parent.Name is "p" or "div" && string.IsNullOrWhiteSpace(parent.InnerText)))
            {
                parent.Remove();
            }
            content = html.DocumentNode.OuterHtml;
        }
    }

    // Strip HTML tags from excerpt
    var excerpt = wpPost.Excerpt.Rendered;
    if (!string.IsNullOrEmpty(excerpt))
    {
        html.LoadHtml(excerpt);
        excerpt = html.DocumentNode.InnerText.Trim();
    }

    // Create Ghost post
    var ghostPost = new GhostPost
    {
        Title = wpPost.Title.Rendered,
        Slug = targetSlug,
        Html = content,
        CustomExcerpt = excerpt,
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

class IdOnly
{
    [JsonProperty("id")] public int Id { get; set; }
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
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        var parts = adminApiKey.Split(':');
        _keyId = parts[0];
        _keySecret = Convert.FromHexString(parts[1]);
    }

    private string GenerateToken()
    {
        var now = DateTime.UtcNow;
        var key = new SymmetricSecurityKey(_keySecret) { KeyId = _keyId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var header = new JwtHeader(credentials)
        {
            ["kid"] = _keyId
        };

        var payload = new JwtPayload(
            issuer: null,
            audience: "/admin/",
            claims: null,
            notBefore: null,
            expires: now.AddMinutes(5),
            issuedAt: now);

        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
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
            var pagePosts = result["posts"]?.ToObject<List<GhostPost>>() ?? [];

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
        return result["tags"]?.ToObject<List<GhostTag>>() ?? [];
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

        // Truncate excerpt to 300 chars max
        var excerpt = post.CustomExcerpt;
        if (excerpt?.Length > 300)
            excerpt = excerpt[..297] + "...";

        var postData = new Dictionary<string, object?>
        {
            ["title"] = post.Title,
            ["slug"] = post.Slug,
            ["html"] = post.Html,
            ["status"] = post.Status,
            ["custom_excerpt"] = excerpt,
            ["published_at"] = post.PublishedAt?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
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
            var fileName = Path.GetFileName(new Uri(imageUrl).LocalPath);
            fileName = Regex.Replace(fileName, @"[^\u0000-\u007F]+", "");
            if (fileName.Length > 100) fileName = fileName[..100] + Path.GetExtension(fileName);

            // Download image with progress
            Console.Write($"  Downloading: {fileName}");
            var downloadStart = DateTime.Now;

            using var downloadResponse = await _http.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead);
            var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
            Console.Write($" ({totalBytes / 1024}KB)");

            await using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();
            var buffer = new byte[81920];
            int bytesRead;
            long totalRead = 0;

            while ((bytesRead = await downloadStream.ReadAsync(buffer)) > 0)
            {
                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                if (totalBytes > 0)
                {
                    var percent = (int)(totalRead * 100 / totalBytes);
                    var elapsed = (DateTime.Now - downloadStart).TotalSeconds;
                    var speed = elapsed > 0 ? totalRead / 1024 / elapsed : 0;
                    Console.Write($"\r  Downloading: {fileName} ({totalBytes / 1024}KB) {percent}% @ {speed:F0}KB/s    ");
                }
            }

            var downloadTime = (DateTime.Now - downloadStart).TotalSeconds;
            var downloadSpeed = downloadTime > 0 ? totalRead / 1024 / downloadTime : 0;
            Console.WriteLine($"\r  Downloaded: {fileName} ({totalRead / 1024}KB) @ {downloadSpeed:F0}KB/s              ");

            var imageBytes = memoryStream.ToArray();

            // Upload to Ghost with progress
            Console.Write($"  Uploading: {fileName} ({imageBytes.Length / 1024}KB)");
            var uploadStart = DateTime.Now;

            var request = CreateRequest(HttpMethod.Post, "images/upload/");

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
            content.Add(fileContent, "file", fileName);

            request.Content = content;

            var response = await _http.SendAsync(request);
            var uploadTime = (DateTime.Now - uploadStart).TotalSeconds;
            var uploadSpeed = uploadTime > 0 ? imageBytes.Length / 1024 / uploadTime : 0;

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"\r  Upload failed: {fileName} - {json}                    ");
                return null;
            }

            var result = JObject.Parse(json);
            var uploadedUrl = result["images"]?[0]?["url"]?.ToString();
            Console.WriteLine($"\r  Uploaded: {fileName} ({imageBytes.Length / 1024}KB) @ {uploadSpeed:F0}KB/s              ");
            return uploadedUrl;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  Error: {ex.Message}");
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