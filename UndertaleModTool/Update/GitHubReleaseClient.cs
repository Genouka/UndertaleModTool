using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UndertaleModTool.Localization;

namespace UndertaleModTool.Update
{
    /// <summary>A downloadable file attached to a GitHub release.</summary>
    public sealed record GitHubReleaseAsset(string Name, string BrowserDownloadUrl);

    /// <summary>The parts of a GitHub release needed to locate an update package.</summary>
    public sealed record GitHubRelease(string TagName, string Body, IReadOnlyList<GitHubReleaseAsset> Assets);

    /// <summary>Minimal GitHub releases client used by the stable update channel.</summary>
    public static class GitHubReleaseClient
    {
        const string Owner = "Genouka";
        const string Repo = "UndertaleModTool";

        public static string LatestReleaseUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        /// <summary>
        /// Fetches the latest published (non-prerelease) release. Returns null when the repository has
        /// no published release yet; throws on network/API errors.
        /// </summary>
        public static async Task<GitHubRelease> FetchLatestReleaseAsync(HttpClient client, CancellationToken cancellationToken = default)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(LatestReleaseUrl, cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                throw new HttpRequestException(LocalizationSource.GetString("Msg_CheckInternetConnection"), e);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        string.Format(LocalizationSource.GetString("Msg_HTTPError"), response.ReasonPhrase));

                using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("tag_name", out JsonElement tagElement)
                    || tagElement.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                string body = root.TryGetProperty("body", out JsonElement bodyElement) && bodyElement.ValueKind == JsonValueKind.String
                    ? bodyElement.GetString() ?? ""
                    : "";

                List<GitHubReleaseAsset> assets = new();
                if (root.TryGetProperty("assets", out JsonElement assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement asset in assetsElement.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("name", out JsonElement nameElement)
                            || nameElement.ValueKind != JsonValueKind.String
                            || !asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
                            || urlElement.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        string name = nameElement.GetString() ?? "";
                        string url = urlElement.GetString() ?? "";
                        if (name.Length == 0 || url.Length == 0)
                            continue;

                        assets.Add(new GitHubReleaseAsset(name, url));
                    }
                }

                return new GitHubRelease(tagElement.GetString() ?? "", body, assets);
            }
        }

        /// <summary>
        /// Asset name suffix that matches the current installation layout:
        /// <c>-Windows-SingleFile.zip</c> for the single-file build, <c>-Windows.zip</c> otherwise.
        /// </summary>
        public static string GetStableAssetSuffix(bool isSingleFile)
            => isSingleFile ? "-Windows-SingleFile.zip" : "-Windows.zip";

        /// <summary>Selects the Windows release asset matching the installation layout, or null.</summary>
        public static GitHubReleaseAsset SelectStableAsset(GitHubRelease release, bool isSingleFile)
        {
            string suffix = GetStableAssetSuffix(isSingleFile);
            return release.Assets.FirstOrDefault(
                asset => asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && asset.Name.StartsWith("UndertaleModTool_", StringComparison.OrdinalIgnoreCase));
        }
    }
}
