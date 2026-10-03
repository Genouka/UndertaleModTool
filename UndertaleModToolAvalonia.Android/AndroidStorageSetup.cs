using System.Runtime.InteropServices;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Locates the "storage setup" (preinstall / 前置) APK that this app points Android 10 users at.
/// <para>
/// Android 10 is the one version this app cannot get direct path-based storage access on by itself:
/// it targets a much newer API level than 29, so granting <c>WRITE_EXTERNAL_STORAGE</c> only buys
/// scoped storage. Asking for the legacy model in the manifest
/// (<c>android:requestLegacyExternalStorage</c>) is honoured by Android 10 but a fresh install still
/// has to be <i>upgraded</i> from something that already had it for
/// <c>android:preserveLegacyExternalStorage</c> to kick in.
/// </para>
/// <para>
/// The preinstall APK is that "something": a tiny targetSdk 29 build of the same package (same
/// application id and version code, see <c>build/AndroidApp.props</c>) that only asks for the
/// runtime storage permission. Installing it over this app and then installing this app over it
/// again carries the legacy storage model - and the already granted permission - over to the real
/// app. The download names below have to match what the publish workflows upload.
/// </para>
/// </summary>
public static class AndroidStorageSetup
{
    /// <summary>Repository the APKs are published in (mirrors <c>UpdateChecker</c>).</summary>
    public const string Owner = "Genouka";
    public const string Repo = "UndertaleModTool";

    /// <summary>Rolling release tag the nightly workflow publishes to.</summary>
    public const string NightlyTag = "nightly";

    /// <summary>
    /// Whether this build came from the stable release workflow
    /// (<c>-p:AndroidPreinstallChannel=stable</c>) rather than from a nightly/local build.
    /// </summary>
    public static bool IsStableChannel =>
#if ANDROID_PREINSTALL_STABLE
        true;
#else
        false;
#endif

    /// <summary>
    /// "arm64" or "x64" - the ABI tag the release asset names carry for this device, matching how
    /// the Android artifacts are published (arm64 for phones, x64 for emulators).
    /// </summary>
    public static string ArchitectureSuffix =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>
    /// File name of the preinstall APK built for this device's ABI. The name deliberately carries no
    /// version or channel: the preinstall APK does the same thing in every version, and a fixed name
    /// keeps <see cref="ApkUrl"/> correct without this app having to know the tag its release was
    /// published under.
    /// </summary>
    public static string ApkFileName =>
        $"UndertaleModToolAvalonia_Android-{ArchitectureSuffix}-Preinstall-Signed.apk";

    /// <summary>
    /// Download URL of the preinstall APK built for this device's ABI. Stable builds read the asset
    /// from the newest release (<c>releases/latest</c>, which skips prereleases such as the rolling
    /// nightly one), nightly builds from the rolling <c>nightly</c> tag.
    /// </summary>
    public static string ApkUrl => IsStableChannel
        ? $"https://github.com/{Owner}/{Repo}/releases/latest/download/{ApkFileName}"
        : $"https://github.com/{Owner}/{Repo}/releases/download/{NightlyTag}/{ApkFileName}";
}
