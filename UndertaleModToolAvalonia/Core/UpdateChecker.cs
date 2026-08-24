using System;
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
/// Checks GitHub for a newer nightly build of UndertaleModToolAvalonia, mirroring the update
/// check of the WPF version of UndertaleModTool.
/// <para>
/// The nightly workflow ("Publish continuous release of UndertaleModTool") publishes the Avalonia
/// builds as <c>UndertaleModToolAvalonia_nightly-&lt;Platform&gt;</c> artifacts and also attaches
/// them as assets to the GitHub release tagged <c>nightly</c>.
/// </para>
/// </summary>
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

    /// <summary>Information about an available nightly build.</summary>
    public sealed record UpdateInfo(long RunId, DateTime UpdatedAt, string ArtifactName,
        string ReleasePageUrl, string ReleaseDownloadUrl, string NightlyLinkDownloadUrl);

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

            return new UpdateInfo(
                RunId: runId,
                UpdatedAt: updatedAt,
                ArtifactName: artifactName,
                ReleasePageUrl: $"https://github.com/{Owner}/{Repo}/releases/tag/{NightlyTag}",
                ReleaseDownloadUrl: $"https://github.com/{Owner}/{Repo}/releases/download/{NightlyTag}/{artifactName}.zip",
                NightlyLinkDownloadUrl: $"https://nightly.link/{Owner}/{Repo}/actions/runs/{runId}/{artifactName}.zip");
        }

        return null;
    }

    /// <summary>Returns whether the given build is at least <see cref="NewerThanMinutes"/> minutes newer than the local install.</summary>
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
}
