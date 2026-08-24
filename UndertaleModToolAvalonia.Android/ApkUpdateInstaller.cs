using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using UndertaleModToolAvalonia;

// Aliases avoid the namespace "UndertaleModToolAvalonia.Android" shadowing "Android.*".
using AndroidUri = global::Android.Net.Uri;
using AndroidSettings = global::Android.Provider.Settings;
using FileProvider = global::AndroidX.Core.Content.FileProvider;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// In-app update support for Android: nightly builds are published as zips containing the APK,
/// which MainViewModel extracts and hands to <see cref="PlatformUpdateInstaller"/>. The APK is
/// served to the system package installer through a FileProvider content:// URI (file:// URIs are
/// rejected when targeting API 24+). Also provides the installed package's last update time as the
/// local build date, where <see cref="Environment.ProcessPath"/> points at no real file.
/// </summary>
public static class ApkUpdateInstaller
{
    /// <summary>Authority suffix of the FileProvider declared in AndroidManifest.xml.</summary>
    const string FileProviderAuthoritySuffix = ".fileprovider";

    /// <summary>Cache folder the update APK is staged into (whitelisted by the FileProvider).</summary>
    const string StagingFolderName = "updates";

    /// <summary>Wires the shared update hooks. Call once from <see cref="MainActivity.OnCreate"/>.</summary>
    public static void Install()
    {
        // The package's install/update time is the best equivalent of the desktop executable's
        // file timestamp (a workflow run's updated_at always precedes installing its build).
        UpdateChecker.LocalBuildTimeUtcOverride = () =>
        {
            PackageInfo? info = Application.Context.PackageManager?
                .GetPackageInfo(Application.Context.PackageName!, (PackageInfoFlags)0);
            long unixSeconds = info is null ? 0
                : info.LastUpdateTime != 0 ? info.LastUpdateTime : info.FirstInstallTime;
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        };

        PlatformUpdateInstaller.InstallPackageAsync = InstallAsync;
    }

    static async Task<bool> InstallAsync(string apkPath)
    {
        Context context = Application.Context;

        // Installing other apps requires the user to allow "install unknown apps" for this app
        // first - open the matching system settings page and have the user retry afterwards.
        if (!context.PackageManager!.CanRequestPackageInstalls())
        {
            OpenInstallPermissionSettings(context);
            return false;
        }

        // Stage the APK into the cache folder whitelisted by the FileProvider (off the UI thread -
        // copying the ~100 MB APK can take a moment on slower storage).
        string stagedApkPath = await Task.Run(() =>
        {
            string stagingDir = Path.Combine(context.CacheDir!.AbsolutePath!, StagingFolderName);
            Directory.CreateDirectory(stagingDir);
            string path = Path.Combine(stagingDir, Path.GetFileName(apkPath)!);
            File.Copy(apkPath, path, true);
            return path;
        });

        AndroidUri apkUri = FileProvider.GetUriForFile(context,
            context.PackageName + FileProviderAuthoritySuffix, new Java.IO.File(stagedApkPath))!;

        using Intent intent = new Intent(Intent.ActionView)!
            .SetDataAndType(apkUri, "application/vnd.android.package-archive")!
            .AddFlags(ActivityFlags.GrantReadUriPermission)!
            .AddFlags(ActivityFlags.NewTask)!;
        context.StartActivity(intent);
        return true;
    }

    static void OpenInstallPermissionSettings(Context context)
    {
        try
        {
            context.StartActivity(new Intent(AndroidSettings.ActionManageUnknownAppSources,
                AndroidUri.Parse("package:" + context.PackageName)).AddFlags(ActivityFlags.NewTask));
            return;
        }
        catch (ActivityNotFoundException)
        {
            // Fall through to the generic app details page.
        }

        try
        {
            context.StartActivity(new Intent(AndroidSettings.ActionApplicationDetailsSettings,
                AndroidUri.Parse("package:" + context.PackageName)).AddFlags(ActivityFlags.NewTask));
        }
        catch (ActivityNotFoundException)
        {
            // Some OEMs lack both intents; nothing sensible left to open.
        }
    }
}
