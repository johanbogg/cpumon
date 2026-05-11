using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

public sealed record ReleaseInfo(
    string TagName,
    string Version,
    string ReleaseUrl,
    string? ServerAssetUrl,
    string? ServerAssetSha256,
    long ServerAssetSize,
    DateTime PublishedAt,
    string? Notes
);

public sealed class UpdateChecker
{
    public const string Repo = "johanbogg/cpumon";
    const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    static readonly HttpClient _http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"cpumon-server/{Proto.AppVersion}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public async Task<ReleaseInfo?> CheckLatestAsync(CancellationToken ct)
    {
        try
        {
            var release = await _http.GetFromJsonAsync<GithubRelease>(ApiUrl, ct).ConfigureAwait(false);
            if (release?.TagName == null) return null;
            if (!Versioning.TryNormalize(release.TagName, out _, out var version)) return null;
            if (!Versioning.IsNewer(version, Proto.AppVersion)) return null;

            string assetPattern = $"cpumon-server-{version}.zip";
            string? assetUrl = null;
            long assetSize = 0;
            if (release.Assets != null)
            {
                foreach (var a in release.Assets)
                {
                    if (a.Name != null && string.Equals(a.Name, assetPattern, StringComparison.OrdinalIgnoreCase))
                    {
                        assetUrl = a.BrowserDownloadUrl;
                        assetSize = a.Size;
                        break;
                    }
                }
            }

            string releaseUrl = release.HtmlUrl ?? $"https://github.com/{Repo}/releases/tag/{release.TagName}";
            return new ReleaseInfo(release.TagName, version, releaseUrl, assetUrl, null, assetSize, release.PublishedAt, release.Body);
        }
        catch (Exception ex)
        {
            LogSink.Debug("UpdateChecker", "Latest release check failed", ex);
            return null;
        }
    }

    sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("published_at")] public DateTime PublishedAt { get; set; }
        [JsonPropertyName("assets")] public GithubAsset[]? Assets { get; set; }
    }

    sealed class GithubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
