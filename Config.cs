using Newtonsoft.Json;

namespace Holomove;

public class SiteConfig
{
    public string SourceDomain { get; set; } = "";
    public string TargetDomain { get; set; } = "";
    public string TargetUsername { get; set; } = "";
    public string TargetPassword { get; set; } = "";
    public string BackupPath { get; set; } = "backup";

    [JsonIgnore] public string SourceWpUrl => $"https://{SourceDomain}";
    [JsonIgnore] public string SourceWpApiUrl => $"{SourceWpUrl}/wp-json/";
    [JsonIgnore] public string TargetWpUrl => $"https://{TargetDomain}";
    [JsonIgnore] public string TargetWpApiUrl => $"{TargetWpUrl}/wp-json/";

    [JsonIgnore] public bool IsConfigured => !string.IsNullOrEmpty(SourceDomain) && !string.IsNullOrEmpty(TargetDomain);

    private const string SettingsFile = "settings.json";

    public static SiteConfig Load()
    {
        if (!File.Exists(SettingsFile)) return new SiteConfig();
        var json = File.ReadAllText(SettingsFile);
        return JsonConvert.DeserializeObject<SiteConfig>(json) ?? new SiteConfig();
    }

    private void Save()
    {
        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(SettingsFile, json);
    }

    public static SiteConfig RunSetup()
    {
        var config = Load();

        Console.WriteLine("\n  WordPress Migration Setup");
        Console.WriteLine("  " + new string('-', 36));

        Console.Write($"  Source domain [{config.SourceDomain}]: ");
        var input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input)) config.SourceDomain = input.Replace("https://", "").Replace("http://", "").TrimEnd('/');

        Console.Write($"  Target domain [{config.TargetDomain}]: ");
        input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input)) config.TargetDomain = input.Replace("https://", "").Replace("http://", "").TrimEnd('/');

        Console.Write($"  Target username [{config.TargetUsername}]: ");
        input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input)) config.TargetUsername = input;

        Console.Write($"  Target password [{(string.IsNullOrEmpty(config.TargetPassword) ? "" : "****")}]: ");
        input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input)) config.TargetPassword = input;

        Console.Write($"  Backup path [{config.BackupPath}]: ");
        input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input)) config.BackupPath = input;

        config.Save();
        Console.WriteLine("\n  Settings saved to settings.json");

        return config;
    }
}
