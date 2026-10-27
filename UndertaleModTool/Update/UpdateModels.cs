using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UndertaleModTool.Update
{
    /// <summary>
    /// Which kind of build the user wants to receive.
    /// </summary>
    /// <remarks>
    /// Nightly builds are only available from GitHub; the configured update source only applies to
    /// stable builds.
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<UpdateChannel>))]
    public enum UpdateChannel
    {
        /// <summary>Published stable releases.</summary>
        Stable = 0,
        /// <summary>Latest successful CI build of the master branch.</summary>
        Nightly = 1,
    }

    /// <summary>
    /// Where stable builds are downloaded from. Nightly builds are always fetched from GitHub.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<UpdateSource>))]
    public enum UpdateSource
    {
        /// <summary>GitHub releases / GitHub Actions artifacts.</summary>
        GitHub = 0,
        /// <summary>MirrorChyan mirror of the stable releases.</summary>
        MirrorChyan = 1,
    }

    /// <summary>
    /// A downloadable update package, already resolved for the configured channel and source.
    /// </summary>
    /// <param name="Channel">Channel the package belongs to.</param>
    /// <param name="Source">Source the package was resolved from.</param>
    /// <param name="VersionLabel">Version label shown to the user (release tag or MirrorChyan version).</param>
    /// <param name="AssetName">Name of the release asset / nightly artifact.</param>
    /// <param name="DownloadUrls">Download URLs in the order they should be tried.</param>
    /// <param name="UpdatedAt">
    /// Nightly workflow update timestamp (UTC), used to decide whether the build is newer than the
    /// installed one. Unused for stable packages, which are compared by version.
    /// </param>
    public sealed record UpdatePackage(
        UpdateChannel Channel,
        UpdateSource Source,
        string VersionLabel,
        string AssetName,
        IReadOnlyList<string> DownloadUrls,
        DateTime UpdatedAt = default);

    /// <summary>
    /// Outcome of an update check.
    /// </summary>
    public enum UpdateCheckStatus
    {
        /// <summary>A package newer than the installed build is available.</summary>
        UpdateAvailable,
        /// <summary>No newer build exists.</summary>
        UpToDate,
        /// <summary>No build (nightly workflow run or stable release) was found.</summary>
        NotFound,
        /// <summary>The build exists, but it has no package for this platform.</summary>
        NoAsset,
        /// <summary>The check itself failed; see <see cref="UpdateCheckResult.ErrorMessage"/>.</summary>
        Failed,
    }

    /// <summary>
    /// Result of <see cref="UpdateChecker.CheckAsync"/>.
    /// </summary>
    /// <param name="Status">Check outcome.</param>
    /// <param name="Package">
    /// The latest package. May be set for <see cref="UpdateCheckStatus.UpToDate"/> when the caller
    /// asked for a package to be prepared regardless of its version.
    /// </param>
    /// <param name="CdkRequired">
    /// MirrorChyan was selected without a CDK, so the GitHub fallback needs the user's confirmation
    /// before downloading.
    /// </param>
    /// <param name="ErrorMessage">Error detail for <see cref="UpdateCheckStatus.Failed"/>.</param>
    /// <param name="IsMirrorChyanError">
    /// Whether <paramref name="ErrorMessage"/> already is a formatted MirrorChyan error (shown as-is
    /// instead of being wrapped in a generic failure message).
    /// </param>
    public sealed record UpdateCheckResult(
        UpdateCheckStatus Status,
        UpdatePackage Package = null,
        bool CdkRequired = false,
        string ErrorMessage = null,
        bool IsMirrorChyanError = false);
}
