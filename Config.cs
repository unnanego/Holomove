namespace Holomove;

public static class Config
{
    // Source WordPress (read-only, no auth needed)
    public const string SourceDomain = "holographica.space";
    public const string SourceWpUrl = $"https://{SourceDomain}";
    public const string SourceWpApiUrl = $"{SourceWpUrl}/wp-json/";

    // Target WordPress (needs JWT authentication)
    public const string TargetWpUrl = "https://new.holographica.space";
    public const string TargetWpApiUrl = $"{TargetWpUrl}/wp-json/";
    public const string TargetWpUsername = "unnanego";
    public const string TargetWpPassword = "cezmx6#DBjJrjL#rOy";

    // Local backup
    public const string BackupPath = "backup";
}
