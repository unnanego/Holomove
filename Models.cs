using Newtonsoft.Json;

namespace Holomove;

public class IdOnly
{
    [JsonProperty("id")] public int Id { get; set; }
}

public class PostSlugInfo
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("slug")] public string Slug { get; set; } = "";
    [JsonProperty("date")] public DateTime Date { get; set; }
}

public class MediaItem
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("source_url")] public string SourceUrl { get; set; } = "";
}
