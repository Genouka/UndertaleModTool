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
        SDL.Context = this;

        base.OnCreate(savedInstanceState);

        // Avalonia.Android's accessibility bridge crashes when a screen-reader/accessibility service
        // walks the automation tree (a provider cached for a peer that conditionally exposes it, e.g.
        // TreeDataGrid rows, later throws "Peer instance does not implement T" and kills the app).
        // The app does not target TalkBack/screen-reader users, so disable the whole bridge.
        AndroidAccessibilityDisabler.Disable(Window!.DecorView);
    }
}