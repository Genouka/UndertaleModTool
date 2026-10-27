using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>A downloadable file attached to a GitHub release.</summary>
public sealed record GitHubReleaseAsset(string Name, string BrowserDownloadUrl);

/// <summary>The parts of a GitHub release needed to locate an update package.</summary>
public sealed record GitHubRelease(string TagName, string Body, IReadOnlyList<GitHubReleaseAsset> Assets);

/// <summary>Minimal GitHub releases client used by the stable update channel.</summary>
public static class GitHubReleaseClient
{
    public static async Task<GitHubRelease?> FetchLatestReleaseAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"https://api.github.com/repos/{UpdateChecker.Owner}/{UpdateChecker.Repo}/releases/latest",
            cancellationToken);

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

        List<GitHubReleaseAsset> assets = [];
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

    /// <summary>Asset name suffix that matches the current platform, or null when unsupported.</summary>
    public static string? GetStableAssetSuffix()
    {
        if (OperatingSystem.IsWindows())
            return "-Windows.zip";
        if (OperatingSystem.IsLinux())
            return "-Linux.zip";
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "-macOS.zip"
                : "-macOS-x86.zip";
        if (OperatingSystem.IsAndroid())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "-Android-arm64-Signed.apk"
                : "-Android-x64-Signed.apk";
        return null;
    }

    /// <summary>Selects the release asset for the current platform, or null when there is none.</summary>
    public static GitHubReleaseAsset? SelectStableAsset(GitHubRelease release)
    {
        string? suffix = GetStableAssetSuffix();
        if (suffix is null)
            return null;

        const string assetPrefix = "UndertaleModToolAvalonia_";
        GitHubReleaseAsset? match = release.Assets.FirstOrDefault(
            asset => asset.Name.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase)
                && asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            return match;

        // Older Android releases only shipped a zip that contains the APK.
        if (OperatingSystem.IsAndroid())
        {
            string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            string archMarker = $"-Android-{arch}";
            return release.Assets.FirstOrDefault(asset =>
                asset.Name.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase)
                && asset.Name.Contains(archMarker, StringComparison.OrdinalIgnoreCase)
                && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }
}
