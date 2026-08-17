using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

// Aliases avoid the namespace "UndertaleModToolAvalonia.Android" shadowing "Android.*".
using AndroidEnvironment = global::Android.OS.Environment;
using AndroidUri = global::Android.Net.Uri;
using AndroidSettings = global::Android.Provider.Settings;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Requests the storage permissions the app needs for direct (path-based) file access on Android:
/// <list type="bullet">
/// <item>Android 6-10 (API 23-29): runtime "WRITE_EXTERNAL_STORAGE" permission dialog.</item>
/// <item>Android 11+ (API 30+): scoped storage applies, so the "All files access"
/// (MANAGE_EXTERNAL_STORAGE) setting must be granted by the user in the system settings.</item>
/// </list>
/// </summary>
public static class StoragePermissionHelper
{
    /// <summary>Request code used for the legacy WRITE_EXTERNAL_STORAGE runtime permission.</summary>
    public const int RequestCode = 0x5331; // "St1"

    static TaskCompletionSource<bool>? s_pendingRequest;

    /// <summary>Whether direct path-based access to shared external storage is currently granted.</summary>
    public static bool IsStorageAccessGranted(Activity activity)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return AndroidEnvironment.IsExternalStorageManager;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return activity.CheckSelfPermission(Manifest.Permission.WriteExternalStorage) == Permission.Granted;
        }
        return true; // API < 23: permissions are granted at install time.
    }

    /// <summary>
    /// Fired from <see cref="MainActivity.OnCreate"/>. Asks the user for the storage access needed
    /// for direct file reads/writes. On API 23-29 this shows the runtime permission dialog; on
    /// API 30+ it opens the system "All files access" settings page (MANAGE_EXTERNAL_STORAGE).
    /// </summary>
    public static void RequestOnStartup(Activity activity)
    {
        if (IsStorageAccessGranted(activity))
            return;

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            OpenAllFilesAccessSettings(activity);
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            s_pendingRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            activity.RequestPermissions(new[] { Manifest.Permission.WriteExternalStorage }, RequestCode);
        }
    }

    /// <summary>
    /// Ensures storage access is granted before a storage operation, awaiting the user's answer on
    /// API 23-29. On API 30+ the "All files access" settings page is opened and <see langword="false"/>
    /// is returned (the result is only known after the user comes back from settings).
    /// </summary>
    public static async Task<bool> EnsureStorageAccessAsync(Activity activity)
    {
        if (IsStorageAccessGranted(activity))
            return true;

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            OpenAllFilesAccessSettings(activity);
            return false;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            s_pendingRequest = tcs;
            activity.RequestPermissions(new[] { Manifest.Permission.WriteExternalStorage }, RequestCode);
            return await tcs.Task;
        }

        return true;
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
            activity.StartActivity(new Intent(AndroidSettings.ActionApplicationDetailsSettings, AndroidUri.Parse("package:" + activity.PackageName)));
        }
    }
}
