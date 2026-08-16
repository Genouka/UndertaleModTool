using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Java.Lang;
using Org.Libsdl.App;
using UndertaleModToolAvalonia;
using AndroidSdl = Org.Libsdl.App.SDL;

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

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder)
            .WithInterFont();
}