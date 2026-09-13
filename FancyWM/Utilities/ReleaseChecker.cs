using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FancyWM.Utilities
{
    public class ReleaseChecker : IDisposable
    {
        private class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string? TagName { get; set; }

            [JsonPropertyName("prerelease")]
            public bool Prerelease { get; set; }

            [JsonPropertyName("published_at")]
            public DateTime? PublishedAt { get; set; }
        }

        private readonly HttpClient m_httpClient;
        private readonly JsonSerializerOptions m_jsonOptions;

        public ReleaseChecker(HttpClient? httpClient = null)
        {
            m_httpClient = httpClient ?? new HttpClient();
            m_jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        public async Task<Version?> GetLatestStableVersionAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken = default)
        {
            var release = await GetLatestStableReleaseAsync(owner, repo, cancellationToken);
            return release?.TagName != null ? new Version(release.TagName.TrimStart('v')) : null;
        }

        private async Task<GitHubRelease?> GetLatestStableReleaseAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken = default)
        {
            var releasesUrl = $"https://api.github.com/repos/{owner}/{repo}/releases";

            using var request = CreateRequest(releasesUrl);
            using var response = await m_httpClient.SendAsync(request, cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var releases = JsonSerializer.Deserialize<GitHubRelease[]>(json, m_jsonOptions);

            if (releases == null || releases.Length == 0)
                return null;

            return FindLatestStableRelease(releases);
        }

        private static HttpRequestMessage CreateRequest(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:130.0) Gecko/20100101 Firefox/130.0");
            request.Headers.Add("Accept", "application/vnd.github.v3+json");
            return request;
        }

        private static GitHubRelease? FindLatestStableRelease(GitHubRelease[] releases)
        {
            foreach (var release in releases)
            {
                if (IsStableRelease(release))
                {
                    return release;
                }
            }
            return null;
        }

        private static bool IsStableRelease(GitHubRelease release)
        {
            if (release.Prerelease)
            {
                return false;
            }

            var tagName = release.TagName?.ToLowerInvariant() ?? string.Empty;
            return !tagName.Contains("rc") && !tagName.Contains("release-candidate");
        }

        public void Dispose()
        {
            m_httpClient?.Dispose();
        }
    }
}
