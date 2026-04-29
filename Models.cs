using Newtonsoft.Json;

namespace Holomove;

// WordPress API models

public class WpPost
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("link")] public string Link { get; set; } = "";
    [JsonProperty("date")] public DateTime Date { get; set; }
    [JsonProperty("modified")] public DateTime Modified { get; set; }
    [JsonProperty("status")] public string Status { get; set; } = "publish";
    [JsonProperty("title")] public WpRendered Title { get; set; } = new();
    [JsonProperty("content")] public WpRendered Content { get; set; } = new();
    [JsonProperty("excerpt")] public WpRendered Excerpt { get; set; } = new();
    [JsonProperty("author")] public int Author { get; set; }
    [JsonProperty("featured_media")] public int FeaturedMedia { get; set; }
    [JsonProperty("tags")] public List<int> Tags { get; set; } = [];
    [JsonProperty("categories")] public List<int> Categories { get; set; } = [];
    [JsonProperty("meta")] public Dictionary<string, object>? Meta { get; set; }
}

public class WpRendered
{
    [JsonProperty("rendered")] public string Rendered { get; set; } = "";
}

public class WpTag
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("description")] public string? Description { get; set; }
}

public class WpCategory
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("description")] public string? Description { get; set; }
}

public class WpUser
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("description")] public string? Description { get; set; }
}

public class IdSlug
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("slug")] public string Slug { get; set; } = "";
}

public class MediaItem
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("source_url")] public string SourceUrl { get; set; } = "";
}

// Human-readable backup models

public class BackupPost
{
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Content { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public DateTime Date { get; set; }
    public string Status { get; set; } = "publish";
    public string AuthorName { get; set; } = "";
    public List<string> TagNames { get; set; } = [];
    public List<string> CategoryNames { get; set; } = [];
    public string? FeaturedMediaUrl { get; set; }
    public List<string> MediaUrls { get; set; } = [];
    public Dictionary<string, object>? Meta { get; set; }
}

public class BackupAuthor
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Email { get; set; }
    public string? Description { get; set; }
}

public class BackupMetadata
{
    public string SiteUrl { get; set; } = "";
    public DateTime ExportDate { get; set; }
    public int PostCount { get; set; }
    public int AuthorCount { get; set; }
    public int TagCount { get; set; }
    public int CategoryCount { get; set; }
}
