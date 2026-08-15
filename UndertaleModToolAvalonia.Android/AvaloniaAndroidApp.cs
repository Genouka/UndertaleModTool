using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Java.Lang;
using Org.Libsdl.App;
using UndertaleModToolAvalonia;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// The Android global application. On create it bootstraps Avalonia (configuring <see cref="App"/>)
/// and installs the window simulation.
/// </summary>
[Application(AllowBackup = false, Theme = "@style/AppTheme", SupportsRtl = true)]
public class AvaloniaAndroidApp : AvaloniaAndroidApplication<App>
{
    public AvaloniaAndroidApp(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
        AndroidWindowHost.Install();
    }

    public override void OnCreate()
    {
        // SDL3 on Android requires its Java bridge to be booted before any SDL API (e.g.
        // AudioPlayer.Init from MainViewModel) can be used. Normally this happens when the app
        // derives its MainActivity from Org.Libsdl.App.SDLActivity; Avalonia needs its own activity
        // instead, so initialize the bridge manually here, before base.OnCreate() starts Avalonia.
        JavaSystem.LoadLibrary("SDL3");
        SDL.SetupJNI();
        SDL.Initialize();

        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder)
            .WithInterFont();
}