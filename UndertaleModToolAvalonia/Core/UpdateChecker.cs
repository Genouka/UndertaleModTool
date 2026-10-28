using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Checks for updates of UndertaleModToolAvalonia. Supports the nightly channel (GitHub Actions
/// runs of the continuous release workflow) and the stable channel (GitHub releases or the
/// MirrorChyan mirror).
/// </summary>
/// <remarks>
/// The nightly workflow ("Publish continuous release of UndertaleModTool") publishes the Avalonia
/// builds as <c>UndertaleModToolAvalonia_nightly-&lt;Platform&gt;</c> artifacts and also attaches
/// them as assets to the GitHub release tagged <c>nightly</c>.
/// </remarks>
public static class UpdateChecker
{
    public const string Owner = "Genouka";
    public const string Repo = "UndertaleModTool";
    public const string WorkflowName = "Publish continuous release of UndertaleModTool";
    public const string NightlyTag = "nightly";

    /// <summary>
    /// A workflow run only counts as an update when it is at least this many minutes newer than
    /// the locally installed build. The run's <c>updated_at</c> is a few minutes later than the
    /// file timestamps inside the published artifact, so a small margin avoids re-prompting for
    /// the build that is currently installed.
    /// </summary>
    public const double NewerThanMinutes = 10;

    /// <summary>Releases page of the repository, listing every published release.</summary>
    public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>
    /// Whether checking for updates makes sense on the current platform. Android is supported too:
    /// the downloaded update APK is handed to the system package installer (see
    /// <see cref="PlatformUpdateInstaller"/>), only iOS has no support.
    /// </summary>
    public static bool IsSupportedPlatform
        => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsAndroid();

    /// <summary>
    /// Optional platform override for the local build date, used where <see cref="Environment.ProcessPath"/>
    /// does not point to a real file with a meaningful timestamp (e.g. an installed APK on Android).
    /// </summary>
    public static Func<DateTime>? LocalBuildTimeUtcOverride;

    /// <summary>Returns the name of the nightly artifact that matches the current platform, or null.</summary>
    public static string? GetArtifactName()
    {
        if (OperatingSystem.IsWindows())
            return "UndertaleModToolAvalonia_nightly-Windows";
        if (OperatingSystem.IsLinux())
            return "UndertaleModToolAvalonia_nightly-Linux";
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "UndertaleModToolAvalonia_nightly-macOS"
                : "UndertaleModToolAvalonia_nightly-macOS-x86";
        if (OperatingSystem.IsAndroid())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "UndertaleModToolAvalonia_nightly-Android-arm64"
                : "UndertaleModToolAvalonia_nightly-Android-x64";
        return null;
    }

    /// <summary>Creates an <see cref="HttpClient"/> configured for the GitHub API.</summary>
    public static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        // Remove the invalid characters from the version string, like the WPF version does.
        Regex invalidChars = new(@"Git:|[ (),/:;<=>?@[\]{}]");
        string version = invalidChars.Replace(App.VersionString, "");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UndertaleModToolAvalonia", version));

        return client;
    }

    /// <summary>
    /// Fetches the latest successful run of the nightly workflow. Returns null when no matching
    /// run was found; throws on network/API errors so callers can decide how to report them.
    /// </summary>
    public static async Task<UpdateInfo?> FetchLatestBuildAsync(HttpClient client)
    {
        string? artifactName = GetArtifactName();
        if (artifactName is null)
            return null;

        using HttpResponseMessage result = await client.GetAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/actions/runs?branch=master&status=success&per_page=20");
        if (!result.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.Format(LocalizationSource.GetString("Msg_HTTPError"), result.ReasonPhrase));

        using JsonDocument doc = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("workflow_runs", out JsonElement runs) || runs.ValueKind != JsonValueKind.Array)
            throw new HttpRequestException(LocalizationSource.GetString("Msg_CheckInternetConnection"));

        foreach (JsonElement run in runs.EnumerateArray())
        {
            if (!run.TryGetProperty("name", out JsonElement nameEl) || nameEl.GetString() != WorkflowName)
                continue;
            if (!run.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number)
                continue;
            if (!run.TryGetProperty("updated_at", out JsonElement dateEl) || dateEl.ValueKind != JsonValueKind.String)
                continue;

            long runId = idEl.GetInt64();
            DateTime updatedAt = dateEl.GetDateTimeOffset().UtcDateTime;
            string version = $"nightly-{runId.ToString(CultureInfo.InvariantCulture)}";

            return new UpdateInfo(
                Channel: UpdateChannel.Nightly,
                Source: UpdateSource.GitHub,
                Version: version,
                AssetName: artifactName,
                DownloadUrls:
                [
                    $"https://github.com/{Owner}/{Repo}/releases/download/{NightlyTag}/{artifactName}.zip",
                    $"https://nightly.link/{Owner}/{Repo}/actions/runs/{runId}/{artifactName}.zip",
                ],
                ReleasePageUrl: $"https://github.com/{Owner}/{Repo}/releases/tag/{NightlyTag}",
                CacheKey: UpdateInfo.BuildCacheKey(UpdateChannel.Nightly, version, artifactName),
                IsApk: false,
                RunId: runId,
                UpdatedAt: updatedAt);
        }

        return null;
    }

    /// <summary>Returns whether the given nightly build is at least <see cref="NewerThanMinutes"/> minutes newer than the local install.</summary>
    public static bool IsNewerThanLocal(UpdateInfo info)
    {
        DateTime localUtc;
        if (LocalBuildTimeUtcOverride is not null)
        {
            localUtc = LocalBuildTimeUtcOverride();
        }
        else
        {
            string? localExe = Environment.ProcessPath;
            if (localExe is null)
                return true;

            localUtc = File.GetLastWriteTime(localExe).ToUniversalTime();
        }

        return info.UpdatedAt.Subtract(localUtc).TotalMinutes > NewerThanMinutes;
    }

    /// <summary>
    /// Version of the currently installed stable release. Uses the tag recorded after the last
    /// stable update, falling back to the assembly informational version.
    /// </summary>
    public static string GetLocalVersion(SettingsFile settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LastStableVersion))
            return settings.LastStableVersion.Trim();

        string informational = UpdateVersion.StripBuildMetadata(App.InformationalVersionString);
        return informational.Length > 0 && char.IsAsciiDigit(informational[0])
            ? informational
            : App.VersionString;
    }

    /// <summary>
    /// Checks the configured channel/source for an update. Nightly builds always come from GitHub;
    /// stable builds come from GitHub or MirrorChyan, with a GitHub fallback when MirrorChyan
    /// fails. Network/API failures are reported through <see cref="UpdateCheckStatus.Failed"/>.
    /// </summary>
    /// <param name="client">HTTP client to use.</param>
    /// <param name="settings">Settings selecting the channel, source and MirrorChyan CDK.</param>
    /// <param name="forcePackage">
    /// When true, returns the package for the latest build even when it is not newer than the
    /// installed version (used by the "Update app" button).
    /// </param>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(HttpClient client, SettingsFile settings, bool forcePackage = false)
    {
        if (!IsSupportedPlatform)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed,
                ErrorMessage: LocalizationSource.GetString("Msg_UpdateNotSupported"));
        }

        try
        {
            UpdateChannel channel = settings.UpdateChannel;
            if (channel == UpdateChannel.Nightly)
                return await CheckNightlyAsync(client, forcePackage);

            UpdateSource source = settings.UpdateSource;
            return source == UpdateSource.MirrorChyan
                ? await CheckStableMirrorChyanAsync(client, settings, forcePackage)
                : await CheckStableGitHubAsync(client, settings, forcePackage, cdkMissing: false);
        }
        catch (Exception e)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: e.Message);
        }
    }

    static async Task<UpdateCheckResult> CheckNightlyAsync(HttpClient client, bool forcePackage)
    {
        UpdateInfo? info = await FetchLatestBuildAsync(client);
        if (info is null)
            return new UpdateCheckResult(UpdateCheckStatus.NotFound);

        bool isNewer = IsNewerThanLocal(info);
        if (!isNewer && !forcePackage)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate);

        return new UpdateCheckResult(
            isNewer ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
            info);
    }

    static async Task<UpdateCheckResult> CheckStableGitHubAsync(
        HttpClient client, SettingsFile settings, bool forcePackage, bool cdkMissing)
    {
        GitHubRelease? release = await GitHubReleaseClient.FetchLatestReleaseAsync(client);
        if (release is null)
            return new UpdateCheckResult(UpdateCheckStatus.NotFound);

        bool isNewer = UpdateVersion.CompareForUpdate(GetLocalVersion(settings), release.TagName) < 0;
        if (!isNewer && !forcePackage)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate);

        UpdateInfo? info = CreateStableGitHubInfo(release);
        if (info is null)
            return new UpdateCheckResult(UpdateCheckStatus.NoAsset);

        return new UpdateCheckResult(
            isNewer ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
            info,
            CdkRequired: cdkMissing && isNewer);
    }

    static UpdateInfo? CreateStableGitHubInfo(GitHubRelease release)
    {
        GitHubReleaseAsset? asset = GitHubReleaseClient.SelectStableAsset(release);
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            return null;

        string tag = release.TagName.Trim();
        return new UpdateInfo(
            Channel: UpdateChannel.Stable,
            Source: UpdateSource.GitHub,
            Version: tag,
            AssetName: asset.Name,
            DownloadUrls: [asset.BrowserDownloadUrl],
            ReleasePageUrl: $"https://github.com/{Owner}/{Repo}/releases/tag/{Uri.EscapeDataString(tag)}",
            CacheKey: UpdateInfo.BuildCacheKey(UpdateChannel.Stable, tag, asset.Name),
            IsApk: asset.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
    }

    static async Task<UpdateCheckResult> CheckStableMirrorChyanAsync(
        HttpClient client, SettingsFile settings, bool forcePackage)
    {
        string cdk = settings.MirrorChyanCdk?.Trim() ?? "";
        if (cdk.Length == 0)
        {
            // Without a CDK MirrorChyan cannot serve the package; use GitHub instead, but tell the
            // caller so it can explain why before downloading.
            return await CheckStableGitHubAsync(client, settings, forcePackage, cdkMissing: true);
        }

        MirrorChyanResponse? response = null;
        string? mirrorChyanError = null;

        try
        {
            response = await MirrorChyanClient.QueryLatestAsync(
                client,
                MirrorChyanClient.GetResourceId(),
                MirrorChyanClient.GetOs(),
                MirrorChyanClient.GetArch(),
                GetLocalVersion(settings),
                cdk);
        }
        catch
        {
            // Never surface the exception: its text could contain the request URL (and the CDK).
            mirrorChyanError = MirrorChyanClient.DescribeNetworkError();
        }

        if (response is not null)
        {
            if (response.Code != 0)
            {
                mirrorChyanError = MirrorChyanClient.DescribeError(response.Code, response.Msg, cdk);
            }
            else if (response.Data is null || string.IsNullOrWhiteSpace(response.Data.Url))
            {
                // The API only returns a URL when a newer build exists.
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
            }
            else
            {
                return await CreateMirrorChyanResultAsync(client, settings, response.Data, forcePackage);
            }
        }

        // Fall back to GitHub. If that fails too, report the MirrorChyan error, as it explains why
        // the configured source was not used.
        try
        {
            return await CheckStableGitHubAsync(client, settings, forcePackage, cdkMissing: false);
        }
        catch
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed,
                ErrorMessage: mirrorChyanError,
                IsMirrorChyanError: true);
        }
    }

    static async Task<UpdateCheckResult> CreateMirrorChyanResultAsync(
        HttpClient client, SettingsFile settings, MirrorChyanRelease release, bool forcePackage)
    {
        string version = string.IsNullOrWhiteSpace(release.VersionName)
            ? GetLocalVersion(settings)
            : release.VersionName.Trim();

        List<string> downloadUrls = [release.Url!];
        string assetName = GitHubReleaseClient.GetStableAssetSuffix()?.TrimStart('-') ?? version;
        string releasePageUrl = ReleasesPageUrl;

        // Best effort: the GitHub asset can be used as a fallback when MirrorChyan's CDN fails.
        try
        {
            GitHubRelease? githubRelease = await GitHubReleaseClient.FetchLatestReleaseAsync(client);
            GitHubReleaseAsset? asset = githubRelease is null ? null : GitHubReleaseClient.SelectStableAsset(githubRelease);
            if (asset is not null)
            {
                assetName = asset.Name;
                releasePageUrl = $"https://github.com/{Owner}/{Repo}/releases/tag/{Uri.EscapeDataString(githubRelease!.TagName.Trim())}";
                if (!string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl)
                    && !string.Equals(asset.BrowserDownloadUrl, release.Url, StringComparison.Ordinal))
                {
                    downloadUrls.Add(asset.BrowserDownloadUrl);
                }
            }
        }
        catch
        {
            // Ignore: the MirrorChyan URL is still usable on its own.
        }

        UpdateInfo info = new(
            Channel: UpdateChannel.Stable,
            Source: UpdateSource.MirrorChyan,
            Version: version,
            AssetName: assetName,
            DownloadUrls: downloadUrls,
            ReleasePageUrl: releasePageUrl,
            CacheKey: UpdateInfo.BuildCacheKey(UpdateChannel.Stable, version, assetName),
            IsApk: OperatingSystem.IsAndroid());

        bool isNewer = UpdateVersion.CompareForUpdate(GetLocalVersion(settings), version) < 0;
        if (!isNewer && !forcePackage)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate);

        return new UpdateCheckResult(
            isNewer ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
            info);
    }
}
