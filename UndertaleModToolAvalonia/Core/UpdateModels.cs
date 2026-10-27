using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UndertaleModToolAvalonia;

/// <summary>Which kind of build the user wants to receive.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateChannel>))]
public enum UpdateChannel
{
    Stable = 0,
    Nightly = 1,
}

/// <summary>Where stable builds are downloaded from. Nightly builds are always fetched from GitHub.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateSource>))]
public enum UpdateSource
{
    GitHub = 0,
    MirrorChyan = 1,
}

/// <summary>
/// A downloadable update package. Nightly packages still carry the workflow run metadata used to
/// decide whether the build is newer than the installed one; stable packages are compared by
/// version instead.
/// </summary>
/// <param name="Channel">Channel the package belongs to.</param>
/// <param name="Source">Source the package was resolved from.</param>
/// <param name="Version">Version label shown to the user (release tag or MirrorChyan version).</param>
/// <param name="AssetName">Name of the release asset / package.</param>
/// <param name="DownloadUrls">Download URLs in the order they should be tried.</param>
/// <param name="ReleasePageUrl">Human-readable release page, used when no package can be installed.</param>
/// <param name="CacheKey">Identifies the exact package for the on-disk download cache.</param>
/// <param name="IsApk">Whether the downloaded file already is an installable APK (Android stable).</param>
/// <param name="RunId">Nightly workflow run id, if this is a nightly package.</param>
/// <param name="UpdatedAt">Nightly workflow update timestamp, if this is a nightly package.</param>
public sealed record UpdateInfo(
    UpdateChannel Channel,
    UpdateSource Source,
    string Version,
    string AssetName,
    IReadOnlyList<string> DownloadUrls,
    string ReleasePageUrl,
    string CacheKey,
    bool IsApk = false,
    long? RunId = null,
    DateTime UpdatedAt = default)
{
    /// <summary>Builds the cache key that identifies a downloaded package.</summary>
    public static string BuildCacheKey(UpdateChannel channel, string version, string assetName)
        => $"{(int)channel}|{version}|{assetName}";
}

/// <summary>Outcome of an update check.</summary>
public enum UpdateCheckStatus
{
    /// <summary>A package newer than the installed build is available.</summary>
    UpdateAvailable,
    /// <summary>No newer build exists.</summary>
    UpToDate,
    /// <summary>No build (nightly run or stable release) was found.</summary>
    NotFound,
    /// <summary>The release exists, but it has no asset for this platform.</summary>
    NoAsset,
    /// <summary>The check itself failed; see <see cref="UpdateCheckResult.ErrorMessage"/>.</summary>
    Failed,
}

/// <summary>Result of <see cref="UpdateChecker.CheckForUpdateAsync"/>.</summary>
/// <param name="Status">Check outcome.</param>
/// <param name="Update">The latest package. May be set for <see cref="UpdateCheckStatus.UpToDate"/>
/// when the caller asked for a package to be prepared regardless of its version.</param>
/// <param name="CdkRequired">MirrorChyan was selected without a CDK, so the GitHub fallback needs
/// the user's confirmation before downloading.</param>
/// <param name="ErrorMessage">Error detail for <see cref="UpdateCheckStatus.Failed"/>.</param>
/// <param name="IsMirrorChyanError">Whether <paramref name="ErrorMessage"/> already is a formatted
/// MirrorChyan error (shown as-is instead of being wrapped in a generic failure message).</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateInfo? Update = null,
    bool CdkRequired = false,
    string? ErrorMessage = null,
    bool IsMirrorChyanError = false);
