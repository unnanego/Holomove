using Newtonsoft.Json;

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
            var randomPassword = Guid.NewGuid().ToString("N")[..16];

            var newAuthor = await CreateOnTarget<WpUser>("users", new
            {
                username = sourceAuthor.Slug,
                name = sourceAuthor.Name,
                slug = sourceAuthor.Slug,
                email = $"{sourceAuthor.Slug}@{_config.SourceDomain}",
                password = randomPassword,
                roles = new[] { "author" },
                description = sourceAuthor.Description ?? ""
            });

            if (newAuthor != null)
            {
                _targetAuthors.Add(newAuthor);
            }
            else
            {
                // May already exist — refresh and find
                var all = await FetchAllPaginated<WpUser>(_config.TargetWpApiUrl, "users", useAuth: true);
                var existing = all.FirstOrDefault(a =>
                    a.Slug.Equals(sourceAuthor.Slug, StringComparison.OrdinalIgnoreCase) ||
                    a.Name.Equals(sourceAuthor.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) _targetAuthors.Add(existing);
            }
        }
    }
}
