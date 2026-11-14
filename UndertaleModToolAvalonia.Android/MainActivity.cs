using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Avalonia.Android;
using Org.Libsdl.App;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// The single launch activity. Avalonia hosts exactly one window here, which is the
/// <see cref="MockStackWindow"/> simulating all of the app's windows (modal stack + tab bar).
/// App builder customization and the <c>WithInterFont()</c> call live on
/// <see cref="AvaloniaAndroidApp.CustomizeAppBuilder"/>.
/// </summary>
[Activity(
    Label = "QiuUTMTv5",
    Theme = "@style/AppTheme",
    MainLauncher = true,
    // Resize the activity for the software keyboard instead of panning the UI behind it: with
    // adjustPan the header and the caret end up off screen. Android 15+ ignores this while the
    // app is laid out edge to edge (targetSdk 35+); there MockStackWindow reserves the IME inset
    // itself, because the platform no longer resizes the window for the keyboard.
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Magick.Native 的打包名与 DllImport 名不一致（见 StageMagickNativeLibraries /
        // MagickNativeLibraryResolver），必须在任何纹理解码触发 Magick 初始化之前挂好解析器，
        // 所以它要先于 base.OnCreate（Avalonia 启动）执行。
        MagickNativeLibraryResolver.Register();
        base.OnCreate(savedInstanceState);
        // SDL3 on Android resolves its Android context (asset manager, file IO, etc.) through
        // SDLActivity.getContext(), which just returns SDL.getContext(). Apps that host SDL inside
        // a non-SDL activity (like Avalonia's) must register the real activity context here so SDL
        // calls like SDL_IOFromFile can initialize the APK asset manager.
        Java.Lang.JavaSystem.LoadLibrary("SDL3");
        SDL.SetupJNI();
        SDL.Initialize();
        SDL.Context = this;

        // Avalonia.Android's accessibility bridge crashes when a screen-reader/accessibility service
        // walks the automation tree (a provider cached for a peer that conditionally exposes it, e.g.
        // TreeDataGrid rows, later throws "Peer instance does not implement T" and kills the app).
        // The app does not target TalkBack/screen-reader users, so disable the whole bridge.
        AndroidAccessibilityDisabler.Disable(Window!.DecorView);

        // Hand the shared UI the storage permissions this app needs for direct (path-based) access
        // to external storage. The request itself runs later, from MainViewModel.OnLoaded: on
        // Android 10 the answer decides whether the user has to be walked through the preinstall
        // APK flow, and that guidance needs the UI (and the user's answer to the system dialog)
        // before it makes sense to show anything.
        PlatformStorageAccess.EnsureAccessAsync = () => StoragePermissionHelper.EnsureAccessAsync(this);
        PlatformStorageAccess.GetPreinstallSetupUrl = () => AndroidStorageSetup.ApkUrl;

        // Wire the shared UI's haptic feedback hooks (long-press / tap gestures in the code editor
        // and room editor) to the platform's haptic feedback API.
        View decorView = Window!.DecorView;
        PlatformHaptics.LongPressFeedback = () => RunOnUiThread(() => decorView.PerformHapticFeedback(FeedbackConstants.LongPress));
        PlatformHaptics.TapFeedback = () => RunOnUiThread(() => decorView.PerformHapticFeedback(FeedbackConstants.VirtualKey));

        // Avalonia only re-applies its IME client when focus changes. Tapping an already focused
        // code editor therefore cannot re-open a keyboard the user dismissed. ShowSoftInput on
        // the focused platform view is the only reliable way to bring it back on Android.
        PlatformSoftKeyboard.ShowHandler = ShowSoftKeyboard;

        // Wire the in-app updater: report the installed package's last update time as the local
        // build date, and hand downloaded update APKs to the system package installer.
        ApkUpdateInstaller.Install();

        // Single-window platform: the embedded Butterscotch runner renders into a native view
        // inside the Avalonia tree instead of opening a window of its own.
        ButterscotchSurfaceHost.Factory = () => new AndroidButterscotchSurfaceView().AsGameSurface();
        ButterscotchSurfaceHost.FullscreenRequested = SetGameFullscreen;
    }

    /// <summary>
    /// Hides or restores the system bars for the embedded game view: WindowInsetsController on
    /// Android 11+, the legacy SystemUiFlags on older versions. Both paths are needed to cover
    /// API 28/29 and 30+, so the deprecation diagnostics for them are suppressed locally.
    /// </summary>
#pragma warning disable CA1422 // both branches are required to cover API 28/29 and 30+
#pragma warning disable CS0618 // the legacy SystemUiVisibility property only exists below API 30
    void SetGameFullscreen(bool fullscreen)
    {
        try
        {
            Window? window = Window;
            View? decor = window?.DecorView;
            if (decor is null)
                return;

            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                window!.SetDecorFitsSystemWindows(!fullscreen);
                IWindowInsetsController? controller = window.InsetsController;
                if (controller is not null)
                {
                    int systemBars = (int) WindowInsets.Type.SystemBars()!;
                    if (fullscreen)
                        controller.Hide(systemBars);
                    else
                        controller.Show(systemBars);
                }
            }
            else
            {
                decor.SystemUiVisibility = fullscreen
                    ? (StatusBarVisibility)(
                        SystemUiFlags.Fullscreen
                        | SystemUiFlags.HideNavigation
                        | SystemUiFlags.ImmersiveSticky
                        | SystemUiFlags.LayoutFullscreen
                        | SystemUiFlags.LayoutHideNavigation
                        | SystemUiFlags.LayoutStable)
                    : (StatusBarVisibility)0;
            }
        }
        catch (Exception)
        {
            // Going fullscreen is best-effort; the game view keeps working without it.
        }
    }
#pragma warning restore CS0618
#pragma warning restore CA1422

    bool ShowSoftKeyboard()
    {
        if (GetSystemService(Context.InputMethodService) is not InputMethodManager inputMethodManager)
            return false;

        View? focusedView = CurrentFocus ?? Window?.DecorView?.FindFocus();
        if (focusedView is null)
            return false;

        return inputMethodManager.ShowSoftInput(focusedView, ShowFlags.Implicit);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        StoragePermissionHelper.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }

    protected override void OnPause()
    {
        // Stop preview audio when the app leaves the foreground. The full SDLActivity pauses SDL
        // audio devices on lifecycle events automatically, but this app only uses the context shim,
        // so nothing would pause it for us (and previews have no business playing in background).
        AudioPlayer.StopAll();

        base.OnPause();
    }

    protected override void OnDestroy()
    {
        // Tear the shared SDL audio stack down while the activity (SDL.Context) is still alive;
        // a later launch within the same process re-initializes everything transparently.
        if (IsFinishing)
            AudioPlayer.Shutdown();

        SDL.Context = null;
        base.OnDestroy();
    }
}