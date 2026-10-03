using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using UndertaleModToolAvalonia;

// Aliases avoid the namespace "UndertaleModToolAvalonia.Android" shadowing "Android.*".
using AndroidEnvironment = global::Android.OS.Environment;
using AndroidUri = global::Android.Net.Uri;
using AndroidSettings = global::Android.Provider.Settings;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Requests the storage permissions the app needs for direct (path-based) file access on Android
/// and reports how that went to the shared UI:
/// <list type="bullet">
/// <item>Android 6-9 (API 23-28): runtime "WRITE_EXTERNAL_STORAGE" permission dialog.</item>
/// <item>Android 10 (API 29): the runtime permission <i>plus</i> the legacy (pre-scoped-storage)
/// storage model. The permission alone is not enough on Android 10, because this app targets a much
/// newer API level than 29 and therefore runs under scoped storage - so a device that cannot get the
/// legacy model is answered with <see cref="StorageAccessResult.NeedsPreinstallSetup"/> and the
/// shared UI walks the user through the preinstall APK flow.</item>
/// <item>Android 11+ (API 30+): scoped storage applies, so the "All files access"
/// (MANAGE_EXTERNAL_STORAGE) setting must be granted by the user in the system settings.</item>
/// </list>
/// </summary>
public static class StoragePermissionHelper
{
    /// <summary>Request code used for the legacy WRITE_EXTERNAL_STORAGE runtime permission.</summary>
    public const int RequestCode = 0x5331; // "St1"

    static TaskCompletionSource<bool>? s_pendingRequest;

    /// <summary>
    /// Whether direct path-based access to shared external storage is actually usable right now.
    /// More strict than <see cref="IsStoragePermissionGranted"/>: on Android 10 the permission is
    /// worthless without the legacy storage model, and on Android 11+ "All files access" is what
    /// counts.
    /// </summary>
    public static bool IsStorageAccessUsable(Activity activity)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            return AndroidEnvironment.IsExternalStorageManager;

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            // Android 10: the permission has to be granted *and* the system has to still be running
            // this app under the legacy storage model.
            return IsStoragePermissionGranted(activity) && AndroidEnvironment.IsExternalStorageLegacy;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(23))
            return IsStoragePermissionGranted(activity);

        return true; // API < 23: permissions are granted at install time.
    }

    /// <summary>
    /// Whether the runtime storage permission this app asks for is granted. Note that on Android 10
    /// and 11+ this is necessary but not sufficient - see <see cref="IsStorageAccessUsable"/>.
    /// </summary>
    public static bool IsStoragePermissionGranted(Activity activity)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
            return activity.CheckSelfPermission(Manifest.Permission.WriteExternalStorage) == Permission.Granted;

        return true; // API < 23: permissions are granted at install time.
    }

    /// <summary>
    /// Implementation behind <see cref="PlatformStorageAccess.EnsureAccessAsync"/> (wired up from
    /// <see cref="MainActivity.OnCreate"/>): asks the user for whatever storage access is missing and
    /// reports the state that came out of it.
    /// </summary>
    public static async Task<StorageAccessResult> EnsureAccessAsync(Activity activity)
    {
        if (IsStorageAccessUsable(activity))
            return StorageAccessResult.Granted;

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // Scoped storage is mandatory here; only the user can flip "All files access".
            OpenAllFilesAccessSettings(activity);
            return StorageAccessResult.NeedsSystemSettings;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !IsStoragePermissionGranted(activity))
            await RequestStoragePermissionAsync(activity);

        if (IsStorageAccessUsable(activity))
            return StorageAccessResult.Granted;

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            // Android 10: the runtime permission cannot enable path-based access on its own here,
            // because the app targets far above API 29 and therefore runs under scoped storage.
            // Installing the targetSdk 29 preinstall APK and then updating back onto this APK is the
            // documented way to end up with the legacy storage model (and the granted permission).
            return StorageAccessResult.NeedsPreinstallSetup;
        }

        // Android 6-9: the runtime permission is the whole story, so a denial can still be fixed by
        // hand in the app's own settings page.
        OpenAppSettings(activity);
        return StorageAccessResult.NeedsSystemSettings;
    }

    /// <summary>
    /// Shows the runtime permission dialog on API 23-29 and completes with the user's answer.
    /// A request that is already in flight is reused instead of stacking two dialogs.
    /// </summary>
    static Task<bool> RequestStoragePermissionAsync(Activity activity)
    {
        if (s_pendingRequest is { } pending)
            return pending.Task;

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pendingRequest = tcs;
        activity.RequestPermissions(new[] { Manifest.Permission.WriteExternalStorage }, RequestCode);
        return tcs.Task;
    }

    /// <summary>Feeds runtime permission results (see <see cref="MainActivity.OnRequestPermissionsResult"/>).</summary>
    public static void OnRequestPermissionsResult(int requestCode, string[]? permissions, Permission[]? grantResults)
    {
        if (requestCode != RequestCode || s_pendingRequest is not { } tcs)
            return;

        s_pendingRequest = null;
        bool granted = grantResults is { Length: > 0 } && grantResults[0] == Permission.Granted;
        tcs.TrySetResult(granted);
    }

    [SupportedOSPlatform("android30.0")]
    static void OpenAllFilesAccessSettings(Activity activity)
    {
        try
        {
            Intent intent = new(AndroidSettings.ActionManageAppAllFilesAccessPermission, AndroidUri.Parse("package:" + activity.PackageName));
            activity.StartActivity(intent);
            return;
        }
        catch (ActivityNotFoundException)
        {
            // Fall through to the generic settings page.
        }

        try
        {
            activity.StartActivity(new Intent(AndroidSettings.ActionManageAllFilesAccessPermission));
        }
        catch (ActivityNotFoundException)
        {
            // Some OEMs lack both intents; at least take the user to the app's own settings.
            OpenAppSettings(activity);
        }
    }

    /// <summary>Opens this app's page in the system settings, where permissions can be edited.</summary>
    static void OpenAppSettings(Activity activity)
    {
        try
        {
            activity.StartActivity(new Intent(AndroidSettings.ActionApplicationDetailsSettings,
                AndroidUri.Parse("package:" + activity.PackageName)));
        }
        catch (ActivityNotFoundException)
        {
            // Nothing sensible left to open.
        }
    }
}
