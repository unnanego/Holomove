using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Holomove;

public partial class WpMigrator
{
    private async Task Authenticate()
    {
        Console.WriteLine("  Authenticating with target WordPress...");

        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"{Config.TargetWpApiUrl}api/v1/token");
        tokenRequest.Content = new StringContent(
            JsonConvert.SerializeObject(new { username = Config.TargetWpUsername, password = Config.TargetWpPassword }),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(tokenRequest);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  Authentication failed: {response.StatusCode}");
            Environment.Exit(1);
        }

        var result = JObject.Parse(json);
        _jwtToken = result["token"]?.ToString()
                 ?? result["jwt_token"]?.ToString()
                 ?? result["access_token"]?.ToString()
                 ?? result["data"]?["token"]?.ToString();

        if (string.IsNullOrEmpty(_jwtToken))
        {
            Console.WriteLine("  Failed to get JWT token from response");
            Environment.Exit(1);
        }

        _targetWp.Auth.SetJWToken(_jwtToken);

        var displayName = result["user_display_name"]?.ToString()
                       ?? result["name"]?.ToString()
                       ?? result["user"]?.ToString()
                       ?? "unknown";
        Console.WriteLine($"  Authenticated as: {displayName}");
    }
}
