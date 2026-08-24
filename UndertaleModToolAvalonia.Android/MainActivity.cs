using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using Org.Libsdl.App;
using Android.Views;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// The single launch activity. Avalonia hosts exactly one window here, which is the
/// <see cref="MockStackWindow"/> simulating all of the app's windows (modal stack + tab bar).
/// App builder customization and the <c>WithInterFont()</c> call live on
/// <see cref="AvaloniaAndroidApp.CustomizeAppBuilder"/>.
/// </summary>
[Activity(
    Label = "UndertaleModToolAvalonia",
    Theme = "@style/AppTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // SDL3 on Android resolves its Android context (asset manager, file IO, etc.) through
        // SDLActivity.getContext(), which just returns SDL.getContext(). Apps that host SDL inside
        // a non-SDL activity (like Avalonia's) must register the real activity context here so SDL
        // calls like SDL_IOFromFile can initialize the APK asset manager.
        Java.Lang.JavaSystem.LoadLibrary("SDL3");
        SDL.SetupJNI();
        SDL.Initialize();
        SDL.Context = this;

        base.OnCreate(savedInstanceState);

        // Avalonia.Android's accessibility bridge crashes when a screen-reader/accessibility service
        // walks the automation tree (a provider cached for a peer that conditionally exposes it, e.g.
        // TreeDataGrid rows, later throws "Peer instance does not implement T" and kills the app).
        // The app does not target TalkBack/screen-reader users, so disable the whole bridge.
        AndroidAccessibilityDisabler.Disable(Window!.DecorView);

        // Ask for direct external-storage access: runtime permission dialog on Android 6-10,
        // "All files access" (MANAGE_EXTERNAL_STORAGE) settings on Android 11+. This lets the app
        // resolve SAF picker results into real paths and read/write external storage directly.
        StoragePermissionHelper.RequestOnStartup(this);

        // Wire the shared UI's haptic feedback hooks (long-press / tap gestures in the code editor
        // and room editor) to the platform's haptic feedback API.
        View decorView = Window!.DecorView;
        PlatformHaptics.LongPressFeedback = () => RunOnUiThread(() => decorView.PerformHapticFeedback(FeedbackConstants.LongPress));
        PlatformHaptics.TapFeedback = () => RunOnUiThread(() => decorView.PerformHapticFeedback(FeedbackConstants.VirtualKey));

        // Wire the in-app updater: report the installed package's last update time as the local
        // build date, and hand downloaded update APKs to the system package installer.
        ApkUpdateInstaller.Install();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        StoragePermissionHelper.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }

    protected override void OnDestroy()
    {
        SDL.Context = null;
        base.OnDestroy();
    }
}