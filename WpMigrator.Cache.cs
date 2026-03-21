using Newtonsoft.Json;
using WordPressPCL.Models;

namespace Holomove;

public partial class WpMigrator
{
    private async Task LoadCache()
    {
        Console.WriteLine("\nLoading cached data...");

        _sourcePosts = await LoadFromCache<List<Post>>("sourcePosts.json") ?? [];
        _sourceTags = await LoadFromCache<List<Tag>>("sourceTags.json") ?? [];
        _sourceAuthors = await LoadFromCache<List<User>>("sourceAuthors.json") ?? [];
        _sourceCategories = await LoadFromCache<List<Category>>("sourceCategories.json") ?? [];
        _targetPosts = await LoadFromCache<List<Post>>("targetPosts.json") ?? [];
        _targetTags = await LoadFromCache<List<Tag>>("targetTags.json") ?? [];
        _targetAuthors = await LoadFromCache<List<User>>("targetAuthors.json") ?? [];
        _targetCategories = await LoadFromCache<List<Category>>("targetCategories.json") ?? [];

        Console.WriteLine($"  Source: {_sourcePosts.Count} posts, {_sourceTags.Count} tags, {_sourceCategories.Count} categories");
        Console.WriteLine($"  Target: {_targetPosts.Count} posts, {_targetTags.Count} tags, {_targetCategories.Count} categories");
    }

    private async Task SaveCache()
    {
        Console.WriteLine("\nSaving cache...");

        await SaveToCache("sourcePosts.json", _sourcePosts);
        await SaveToCache("sourceTags.json", _sourceTags);
        await SaveToCache("sourceAuthors.json", _sourceAuthors);
        await SaveToCache("sourceCategories.json", _sourceCategories);
        await SaveToCache("targetPosts.json", _targetPosts);
        await SaveToCache("targetTags.json", _targetTags);
        await SaveToCache("targetAuthors.json", _targetAuthors);
        await SaveToCache("targetCategories.json", _targetCategories);
    }

    private async Task<T?> LoadFromCache<T>(string fileName) where T : class
    {
        var path = Path.Combine(_dataFolder, fileName);
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonConvert.DeserializeObject<T>(json);
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveToCache<T>(string fileName, T data)
    {
        var path = Path.Combine(_dataFolder, fileName);
        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        await File.WriteAllTextAsync(path, json);
    }
}
