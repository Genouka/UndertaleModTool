using System;
using System.Threading.Tasks;

namespace UndertaleModToolAvalonia;

/// <summary>Outcome of asking the platform for direct (path-based) access to shared storage.</summary>
public enum StorageAccessResult
{
    /// <summary>Direct path-based storage access is usable (or the platform does not need any).</summary>
    Granted,

    /// <summary>
    /// Still not usable, and the platform has sent the user somewhere to fix it (Android 11+ opens
    /// the "All files access" system settings page, Android 6-9 the app's own settings page). The
    /// result is only known once the user comes back, so nothing else can be shown for it.
    /// </summary>
    NeedsSystemSettings,

    /// <summary>
    /// Android 10 without legacy external storage: granting the runtime permission is not enough
    /// here, because the app targets a much newer API level and therefore runs under scoped
    /// storage. The user has to install the "storage setup" (preinstall) APK first and then install
    /// the real APK right on top of it - see <see cref="PreinstallSetupUrl"/>. The shared UI shows
    /// the step-by-step instructions for that flow when it receives this result.
    /// </summary>
    NeedsPreinstallSetup,
}

/// <summary>
/// Bridge for the storage permissions the shared UI needs: the whole app is path-based, so it
/// requires real read/write access to shared external storage, not SAF streams.
/// <para>
/// Desktop builds leave the callbacks unset and every platform check is skipped. The Android
/// implementation lives in <c>UndertaleModToolAvalonia.Android/StoragePermissionHelper.cs</c> and is
/// wired up from <c>MainActivity.OnCreate</c>.
/// </para>
/// </summary>
public static class PlatformStorageAccess
{
    /// <summary>
    /// Platform callback that makes sure direct path-based shared storage access is available,
    /// asking the user for the missing permission/state first if needed. Never returns
    /// <see cref="StorageAccessResult.NeedsPreinstallSetup"/> on a platform without a preinstall APK.
    /// </summary>
    public static Func<Task<StorageAccessResult>>? EnsureAccessAsync;

    /// <summary>
    /// Platform callback returning the download URL of the "storage setup" (preinstall) APK that
    /// makes <see cref="StorageAccessResult.NeedsPreinstallSetup"/> solvable, or <see langword="null"/>
    /// when the current platform/build has no such APK (only Android does).
    /// </summary>
    public static Func<string?>? GetPreinstallSetupUrl;
}
