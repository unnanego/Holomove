using Newtonsoft.Json;
using WordPressPCL.Models;

namespace Holomove;

public partial class WpMigrator
{
    private async Task MigrateAuthors()
    {
        Console.WriteLine("\nMigrating authors...");

        var authorsToCreate = new List<User>();

        foreach (var sourceAuthor in _sourceAuthors)
        {
            var existingTarget = _targetAuthors.FirstOrDefault(a =>
                a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));

            if (existingTarget == null)
            {
                authorsToCreate.Add(sourceAuthor);
            }
        }

        if (authorsToCreate.Count == 0)
        {
            Console.WriteLine("All authors already exist on target.");
            return;
        }

        Console.WriteLine($"Creating {authorsToCreate.Count} missing author(s)...");

        foreach (var sourceAuthor in authorsToCreate)
        {
            try
            {
                Console.Write($"  Creating author: {sourceAuthor.Name} ({sourceAuthor.Slug})...");

                var newAuthor = await CreateAuthorOnTarget(sourceAuthor);
                if (newAuthor != null)
                {
                    _targetAuthors.Add(newAuthor);
                    _targetAuthorsDict[newAuthor.Slug] = newAuthor;
                    Console.WriteLine($" ID: {newAuthor.Id}");
                }
                else
                {
                    Console.WriteLine(" Failed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Error: {ex.Message}");
            }
        }
    }

    private async Task<User?> CreateAuthorOnTarget(User sourceAuthor)
    {
        try
        {
            const string url = $"{Config.TargetWpApiUrl}wp/v2/users";
            var randomPassword = System.Guid.NewGuid().ToString("N")[..16];

            var response = await PostJsonAsync(url, new
            {
                username = sourceAuthor.Slug,
                name = sourceAuthor.Name,
                slug = sourceAuthor.Slug,
                email = $"{sourceAuthor.Slug}@holographica.space",
                password = randomPassword,
                roles = new[] { "author" },
                description = sourceAuthor.Description ?? ""
            });
            var json = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode) return JsonConvert.DeserializeObject<User>(json);
            if (json.Contains("existing_user_login") || json.Contains("existing_user_email"))
            {
                Console.Write(" (already exists, fetching)");
                var refreshedAuthors = await FetchAllFromApi<User>("users");
                if (refreshedAuthors != null)
                {
                    var existing = refreshedAuthors.FirstOrDefault(a =>
                        a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
                        a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));
                    return existing;
                }
            }
            Console.WriteLine($"\n    API Error: {json}");
            return null;

        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n    Exception: {ex.Message}");
            return null;
        }
    }

    private async Task UpdateExistingPostsAuthors()
    {
        Console.WriteLine("\nChecking existing posts for author mismatches...");

        var postsToUpdate = new List<(Post targetPost, int correctAuthorId, string authorName)>();

        var targetPostsBySlug = _targetPosts.ToDictionary(p => p.Slug, p => p);

        foreach (var sourcePost in _sourcePosts)
        {
            if (!targetPostsBySlug.TryGetValue(sourcePost.Slug, out var targetPost))
                continue;

            if (!_sourceUsersDict.TryGetValue(sourcePost.Author, out var sourceAuthor))
                continue;

            var targetAuthor = _targetAuthors.FirstOrDefault(a =>
                a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));

            if (targetAuthor == null)
            {
                Console.WriteLine($"  Warning: No target author found for '{sourceAuthor.Name}' (post: {sourcePost.Slug})");
                continue;
            }

            if (targetPost.Author == targetAuthor.Id)
                continue;

            postsToUpdate.Add((targetPost, targetAuthor.Id, targetAuthor.Name));
        }

        if (postsToUpdate.Count == 0)
        {
            Console.WriteLine("All existing posts have correct authors.");
            return;
        }

        Console.WriteLine($"Updating {postsToUpdate.Count} post(s) with correct authors...");

        foreach (var (targetPost, correctAuthorId, authorName) in postsToUpdate)
        {
            try
            {
                Console.Write($"  Updating '{targetPost.Slug}' -> author: {authorName}...");

                var success = await UpdatePostAuthor(targetPost.Id, correctAuthorId);
                if (success)
                {
                    targetPost.Author = correctAuthorId;
                    Console.WriteLine(" Done");
                }
                else
                {
                    Console.WriteLine(" Failed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Error: {ex.Message}");
            }
        }
    }

    private async Task<bool> UpdatePostAuthor(int postId, int authorId)
    {
        try
        {
            var response = await PostJsonAsync($"{Config.TargetWpApiUrl}wp/v2/posts/{postId}", new { author = authorId });

            if (response.IsSuccessStatusCode) return true;
            var error = await response.Content.ReadAsStringAsync();
            Console.Write($" (API error: {error})");
            return false;
        }
        catch
        {
            return false;
        }
    }
}
