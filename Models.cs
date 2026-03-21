using Newtonsoft.Json;

namespace Holomove;

public class IdOnly
{
    [JsonProperty("id")] public int Id { get; set; }
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
