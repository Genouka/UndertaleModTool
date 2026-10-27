using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UndertaleModTool.Localization;

namespace UndertaleModTool.Update
{
    /// <summary>
    /// Resolves update packages for the WPF app. The nightly channel keeps using the GitHub Actions
    /// artifacts of the continuous release workflow (downloaded through nightly.link), while the
    /// stable channel uses GitHub releases or the MirrorChyan mirror.
    /// </summary>
    public static class UpdateChecker
    {
        public const string Owner = "genouka";
        public const string Repo = "UndertaleModTool";
        public const string WorkflowName = "Publish continuous release of UndertaleModTool";

        /// <summary>Prefix of the Windows nightly artifact names published by the workflow.</summary>
        public const string NightlyArtifactPrefix = "UndertaleModTool_nightly-Windows";

        /// <summary>
        /// A nightly run only counts as an update when it is at least this many minutes newer than
        /// the locally installed build. The run's <c>updated_at</c> is a few minutes later than the
        /// file timestamps inside the published artifact, so a small margin avoids re-prompting for
        /// the build that is currently installed.
        /// </summary>
        public const double NewerThanMinutes = 10;

        /// <summary>Returns the name of the nightly artifact that matches the installation layout.</summary>
        public static string GetNightlyArtifactName(bool isSingleFile)
            => NightlyArtifactPrefix + (isSingleFile ? "-SingleFile" : "");

        /// <summary>Creates an <see cref="HttpClient"/> configured for the GitHub API.</summary>
        public static HttpClient CreateHttpClient()
        {
            HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            // Remove the invalid characters (everything within square brackets and friends) from the version string.
            Regex invalidChars = new(@"Git:|[ (),/:;<=>?@[\]{}]");
            string version = invalidChars.Replace(MainWindow.Version ?? "", "");
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UndertaleModTool", version));

            return client;
        }

        /// <summary>
        /// Version of the currently installed stable release. Uses the tag recorded after the last
        /// stable update, falling back to the numeric core of the assembly version.
        /// </summary>
        public static string GetLocalVersion()
        {
            string recorded = Settings.Instance?.LastStableVersion;
            if (!string.IsNullOrWhiteSpace(recorded))
                return recorded.Trim();

            return UpdateVersion.GetVersionCore(MainWindow.Version);
        }

        /// <summary>
        /// Checks the configured channel/source for an update. Nightly builds always come from
        /// GitHub; stable builds come from GitHub or MirrorChyan, with a GitHub fallback when
        /// MirrorChyan fails. Network/API failures are reported through
        /// <see cref="UpdateCheckStatus.Failed"/>.
        /// </summary>
        /// <param name="client">HTTP client to use.</param>
        /// <param name="isSingleFile">Whether the app runs from the single-file build.</param>
        /// <param name="forcePackage">
        /// When true, returns the package for the latest build even when it is not newer than the
        /// installed version (used by the "Update app" button).
        /// </param>
        /// <param name="localBuildTime">Last write time of the installed executable (nightly only).</param>
        public static async Task<UpdateCheckResult> CheckAsync(
            HttpClient client, bool isSingleFile, bool forcePackage, DateTime localBuildTime)
        {
            try
            {
                if ((Settings.Instance?.UpdateChannel ?? UpdateChannel.Nightly) == UpdateChannel.Nightly)
                    return await CheckNightlyAsync(client, isSingleFile, forcePackage, localBuildTime);

                return await CheckStableAsync(client, isSingleFile, forcePackage);
            }
            catch (Exception e)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: e.Message);
            }
        }

        /// <summary>
        /// Fetches the latest successful nightly run and its Windows artifact. Returns null when no
        /// matching run exists. The returned package has no download URL when the run has no matching
        /// artifact.
        /// </summary>
        public static async Task<UpdatePackage> FetchLatestNightlyAsync(HttpClient client, bool isSingleFile)
        {
            string artifactName = GetNightlyArtifactName(isSingleFile);
            string baseUrl = $"https://api.github.com/repos/{Owner}/{Repo}/actions/";

            using HttpResponseMessage result = await GetAsync(client, baseUrl + "runs?branch=master&status=success&per_page=20");
            if (!result.IsSuccessStatusCode)
                throw new HttpRequestException(string.Format(LocalizationSource.GetString("Msg_HTTPError"), result.ReasonPhrase));

            long? runId = null;
            DateTime updatedAt = default;

            using (JsonDocument doc = JsonDocument.Parse(await result.Content.ReadAsStringAsync()))
            {
                if (!doc.RootElement.TryGetProperty("workflow_runs", out JsonElement runs)
                    || runs.ValueKind != JsonValueKind.Array)
                {
                    throw new HttpRequestException(LocalizationSource.GetString("Msg_CheckInternetConnection"));
                }

                foreach (JsonElement run in runs.EnumerateArray())
                {
                    if (!run.TryGetProperty("name", out JsonElement nameElement)
                        || nameElement.ValueKind != JsonValueKind.String
                        || nameElement.GetString() != WorkflowName)
                    {
                        continue;
                    }
                    if (!run.TryGetProperty("id", out JsonElement idElement)
                        || !idElement.TryGetInt64(out long id))
                    {
                        continue;
                    }

                    if (run.TryGetProperty("updated_at", out JsonElement dateElement)
                        && dateElement.ValueKind == JsonValueKind.String
                        && dateElement.TryGetDateTimeOffset(out DateTimeOffset runDate))
                    {
                        updatedAt = runDate.UtcDateTime;
                    }

                    runId = id;
                    break;
                }
            }

            if (runId is null)
                return null;

            string version = "nightly-" + runId.Value.ToString(CultureInfo.InvariantCulture);
            string downloadUrl = await FindNightlyArtifactDownloadUrlAsync(client, baseUrl, runId.Value, artifactName);

            return new UpdatePackage(
                Channel: UpdateChannel.Nightly,
                Source: UpdateSource.GitHub,
                VersionLabel: version,
                AssetName: artifactName,
                DownloadUrls: downloadUrl is null ? Array.Empty<string>() : new[] { downloadUrl },
                UpdatedAt: updatedAt);
        }

        /// <summary>Whether the nightly build is at least <see cref="NewerThanMinutes"/> minutes newer than the local install.</summary>
        public static bool IsNewerThanLocal(UpdatePackage package, DateTime localBuildTime)
            => package.UpdatedAt.Subtract(localBuildTime.ToUniversalTime()).TotalMinutes > NewerThanMinutes;

        static async Task<UpdateCheckResult> CheckNightlyAsync(
            HttpClient client, bool isSingleFile, bool forcePackage, DateTime localBuildTime)
        {
            UpdatePackage package = await FetchLatestNightlyAsync(client, isSingleFile);
            if (package is null)
                return new UpdateCheckResult(UpdateCheckStatus.NotFound);
            if (package.DownloadUrls.Count == 0)
                return new UpdateCheckResult(UpdateCheckStatus.NoAsset, package);

            bool isNewer = IsNewerThanLocal(package, localBuildTime);
            if (!isNewer && !forcePackage)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);

            return new UpdateCheckResult(isNewer ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate, package);
        }

        static async Task<UpdateCheckResult> CheckStableAsync(HttpClient client, bool isSingleFile, bool forcePackage)
        {
            if ((Settings.Instance?.UpdateSource ?? UpdateSource.GitHub) == UpdateSource.MirrorChyan)
                return await CheckStableMirrorChyanAsync(client, isSingleFile, forcePackage);

            return await CheckStableGitHubAsync(client, isSingleFile, forcePackage, cdkMissing: false);
        }

        static async Task<UpdateCheckResult> CheckStableGitHubAsync(
            HttpClient client, bool isSingleFile, bool forcePackage, bool cdkMissing)
        {
            GitHubRelease release = await GitHubReleaseClient.FetchLatestReleaseAsync(client);
            if (release is null)
                return new UpdateCheckResult(UpdateCheckStatus.NotFound);

            GitHubReleaseAsset asset = GitHubReleaseClient.SelectStableAsset(release, isSingleFile);
            if (asset is null)
                return new UpdateCheckResult(UpdateCheckStatus.NoAsset);

            UpdatePackage package = CreateStableGitHubPackage(release, asset);
            bool isNewer = UpdateVersion.CompareForUpdate(GetLocalVersion(), package.VersionLabel) < 0;
            if (!isNewer && !forcePackage)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);

            return new UpdateCheckResult(
                isNewer ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
                package,
                CdkRequired: cdkMissing && isNewer);
        }

        static UpdatePackage CreateStableGitHubPackage(GitHubRelease release, GitHubReleaseAsset asset)
        {
            return new UpdatePackage(
                Channel: UpdateChannel.Stable,
                Source: UpdateSource.GitHub,
                VersionLabel: release.TagName.Trim(),
                AssetName: asset.Name,
                DownloadUrls: new[] { asset.BrowserDownloadUrl });
        }

        static async Task<UpdateCheckResult> CheckStableMirrorChyanAsync(
            HttpClient client, bool isSingleFile, bool forcePackage)
        {
            string cdk = Settings.Instance?.MirrorChyanCdk?.Trim() ?? "";
            if (cdk.Length == 0)
            {
                // Without a CDK MirrorChyan cannot serve the package; use GitHub instead, but tell the
                // caller so it can explain why before downloading.
                return await CheckStableGitHubAsync(client, isSingleFile, forcePackage, cdkMissing: true);
            }

            MirrorChyanResponse response = null;
            string mirrorChyanError = null;

            try
            {
                response = await MirrorChyanClient.QueryLatestAsync(
                    client, MirrorChyanClient.GetResourceId(isSingleFile), GetLocalVersion(), cdk);
            }
            catch
            {
                // Never surface the exception: its text could contain the request URL (and the CDK).
                mirrorChyanError = MirrorChyanClient.DescribeNetworkError();
            }

            if (response is not null)
            {
                if (!response.IsSuccess)
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
                    return await CreateMirrorChyanResultAsync(client, isSingleFile, response.Data, forcePackage);
                }
            }

            // Fall back to GitHub. If that fails too, report the MirrorChyan error, as it explains why
            // the configured source was not used.
            try
            {
                return await CheckStableGitHubAsync(client, isSingleFile, forcePackage, cdkMissing: false);
            }
            catch
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    ErrorMessage: mirrorChyanError,
                    IsMirrorChyanError: true);
            }
        }

        static async Task<UpdateCheckResult> CreateMirrorChyanResultAsync(
            HttpClient client, bool isSingleFile, MirrorChyanRelease release, bool forcePackage)
        {
            string version = string.IsNullOrWhiteSpace(release.VersionName)
                ? GetLocalVersion()
                : release.VersionName.Trim();

            List<string> downloadUrls = new() { release.Url };
            string assetName = GitHubReleaseClient.GetStableAssetSuffix(isSingleFile).TrimStart('-');

            // Best effort: the GitHub asset can be used as a fallback when MirrorChyan's CDN fails.
            try
            {
                GitHubRelease githubRelease = await GitHubReleaseClient.FetchLatestReleaseAsync(client);
                GitHubReleaseAsset asset = githubRelease is null
                    ? null
                    : GitHubReleaseClient.SelectStableAsset(githubRelease, isSingleFile);
                if (asset is not null)
                {
                    assetName = asset.Name;
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

            UpdatePackage package = new(
                Channel: UpdateChannel.Stable,
                Source: UpdateSource.MirrorChyan,
                VersionLabel: version,
                AssetName: assetName,
                DownloadUrls: downloadUrls);

            bool isNewer = UpdateVersion.CompareForUpdate(GetLocalVersion(), version) < 0;
            if (!isNewer && !forcePackage)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);

            return new UpdateCheckResult(isNewer ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate, package);
        }

        static async Task<string> FindNightlyArtifactDownloadUrlAsync(
            HttpClient client, string baseUrl, long runId, string artifactName)
        {
            using HttpResponseMessage result = await GetAsync(client, $"{baseUrl}runs/{runId}/artifacts");
            if (!result.IsSuccessStatusCode)
                throw new HttpRequestException(string.Format(LocalizationSource.GetString("Msg_HTTPError"), result.ReasonPhrase));

            using JsonDocument doc = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("artifacts", out JsonElement artifacts)
                || artifacts.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement artifact in artifacts.EnumerateArray())
            {
                if (!artifact.TryGetProperty("name", out JsonElement nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || nameElement.GetString() != artifactName)
                {
                    continue;
                }

                if (!artifact.TryGetProperty("archive_download_url", out JsonElement urlElement)
                    || urlElement.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                string archiveUrl = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(archiveUrl))
                    return null;

                // GitHub doesn't let anonymous users download artifacts, so let's use nightly.link.
                return archiveUrl.Replace("api.github.com/repos", "nightly.link").Replace("/zip", ".zip");
            }

            return null;
        }

        static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url)
        {
            try
            {
                return await client.GetAsync(url);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                throw new HttpRequestException(LocalizationSource.GetString("Msg_CheckInternetConnection"), e);
            }
        }
    }
}
