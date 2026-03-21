using Newtonsoft.Json;
using WordPressPCL.Models;

namespace Holomove;

public partial class WpMigrator
{
    private async Task SyncAuthors()
    {
        Console.WriteLine("\n  Syncing authors...");

        var authorsToCreate = _sourceAuthors
            .Where(sa => !_targetAuthors.Any(ta =>
                ta.Slug.Equals(sa.Slug, StringComparison.OrdinalIgnoreCase) ||
                ta.Name.Equals(sa.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (authorsToCreate.Count == 0)
        {
            Console.WriteLine("  All authors in sync.");
            return;
        }

        Console.WriteLine($"  Creating {authorsToCreate.Count} missing author(s)...");

        foreach (var sourceAuthor in authorsToCreate)
        {
            try
            {
                var newAuthor = await CreateAuthorOnTarget(sourceAuthor);
                if (newAuthor != null)
                {
                    _targetAuthors.Add(newAuthor);
                }
            }
            catch { /* skip */ }
        }
    }

    private static readonly string[] StringArray = ["author"];

    private async Task<User?> CreateAuthorOnTarget(User sourceAuthor)
    {
        try
        {
            var randomPassword = System.Guid.NewGuid().ToString("N")[..16];

            var response = await PostJsonAsync($"{Config.TargetWpApiUrl}wp/v2/users", new
            {
                username = sourceAuthor.Slug,
                name = sourceAuthor.Name,
                slug = sourceAuthor.Slug,
                email = $"{sourceAuthor.Slug}@{Config.SourceDomain}",
                password = randomPassword,
                roles = StringArray,
                description = sourceAuthor.Description ?? ""
            });

            var json = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
                return JsonConvert.DeserializeObject<User>(json);

            if (!json.Contains("existing_user_login") && !json.Contains("existing_user_email")) return null;
            
            var refreshedAuthors = await FetchAllFromApi<User>("users");
            return refreshedAuthors?.FirstOrDefault(a =>
                a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));

        }
        catch
        {
            return null;
        }
    }
}
