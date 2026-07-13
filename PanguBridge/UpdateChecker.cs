using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanguBridge;

/// <summary>
/// Manual "Check for Update" support (Options tab) - never runs automatically, never downloads
/// anything. Asks GitHub's REST API for the latest published release tag and compares it
/// against version.txt (AppVersion.Raw); the caller opens the plain releases page (not a
/// specific tag URL) if the user chooses to.
/// </summary>
public static class UpdateChecker
{
    private const string RepoOwner = "AbbieDoobie";
    private const string RepoName = "PanguBridge";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    public const string RepoUrl = $"https://github.com/{RepoOwner}/{RepoName}";
    public const string ReleasesPageUrl = $"{RepoUrl}/releases";

    public readonly record struct Result(bool Success, bool UpdateAvailable, string? LatestVersion);

    public static async Task<Result> CheckAsync()
    {
        try
        {
            using var client = new HttpClient();
            // api.github.com rejects requests with no User-Agent header.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PanguBridge-UpdateCheck");

            using var stream = await client.GetStreamAsync(LatestReleaseApiUrl);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream);
            string? tag = release?.TagName;
            if (string.IsNullOrWhiteSpace(tag)) return new Result(false, false, null);

            string latestRaw = tag.TrimStart('v', 'V');
            string? currentRaw = AppVersion.Raw;

            if (currentRaw is null
             || !Version.TryParse(latestRaw, out var latest)
             || !Version.TryParse(currentRaw, out var current))
            {
                return new Result(false, false, null);
            }

            return new Result(true, latest > current, latestRaw);
        }
        catch
        {
            return new Result(false, false, null);
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
    }
}
